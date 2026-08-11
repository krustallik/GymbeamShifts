using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public sealed class SafeDockerLogReader(
    HttpClient httpClient,
    TimeSpan requestTimeout,
    int maximumResponseBytes,
    string? selfContainerId) : IDockerLogReader
{
    private const string ListPath = "/containers/json?all=true&filters=%7B%22label%22%3A%5B%22com.gymbeam.managed%3Dtrue%22%5D%7D";
    private static readonly UTF8Encoding TolerantUtf8 = new(false, false);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex SensitiveKeyValue = new(
        "(?<prefix>[\\w.-]*(?:password|passwd|secret|token|api[_-]?key|authorization|cookie|chat[_-]?id|username|email|login)[\\w.-]*[\\\"']?\\s*[:=]\\s*).*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex TelegramToken = new(
        "\\b[0-9]{6,12}:[A-Za-z0-9_-]{20,}\\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex BearerToken = new(
        "(?<=\\bBearer\\s)[A-Za-z0-9._~+/-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex SensitiveHeader = new(
        "(?<prefix>^(?:authorization|cookie|set-cookie)\\s*:\\s*).+$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex JwtToken = new(
        "\\beyJ[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\.[A-Za-z0-9_-]+\\b",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex TelegramChannelId = new(
        "(?<![0-9])-100[0-9]{6,13}(?![0-9])",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly HashSet<string> ReservedIdentities = new(StringComparer.Ordinal)
    {
        "caddy",
        "gymbeam-caddy",
        "gymbeam-admin-manager"
    };

    public async Task<DockerLogResult> ReadAsync(
        ManagedBot bot,
        int tail,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeBot(bot) || tail is < 1 or > 500)
        {
            return new DockerLogResult("invalid_request", string.Empty);
        }

        LocateResult located = await LocateAsync(bot, cancellationToken);
        if (located.ContainerId is null)
        {
            return new DockerLogResult(located.Outcome, string.Empty);
        }

        BytesResult response = await GetBytesAsync(
            $"/containers/{located.ContainerId}/logs?stdout=true&stderr=true&tail={tail}&timestamps=true",
            cancellationToken);
        if (response.Bytes is null)
        {
            return new DockerLogResult(response.Outcome, string.Empty);
        }

        string decoded = TolerantUtf8.GetString(ExtractDockerPayload(response.Bytes));
        return TryRedact(decoded, out string redacted)
            ? new DockerLogResult("succeeded", redacted)
            : new DockerLogResult("redaction_failed", string.Empty);
    }

    internal static bool TryRedact(string decoded, out string redacted)
    {
        try
        {
            redacted = SensitiveHeader.Replace(decoded, "${prefix}[REDACTED]");
            redacted = SensitiveKeyValue.Replace(redacted, "${prefix}[REDACTED]");
            redacted = TelegramToken.Replace(redacted, "[REDACTED]");
            redacted = BearerToken.Replace(redacted, "[REDACTED]");
            redacted = JwtToken.Replace(redacted, "[REDACTED]");
            redacted = TelegramChannelId.Replace(redacted, "[REDACTED]");
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            redacted = string.Empty;
            return false;
        }
    }

    private async Task<LocateResult> LocateAsync(ManagedBot bot, CancellationToken cancellationToken)
    {
        BytesResult listResponse = await GetBytesAsync(ListPath, cancellationToken);
        JsonDocument? list = ParseJson(listResponse.Bytes);
        if (list is null)
        {
            return new LocateResult(null, listResponse.Outcome);
        }

        string[] candidates;
        using (list)
        {
            candidates = ReadCandidates(list.RootElement, bot).Take(2).ToArray();
        }

        if (candidates.Length == 0)
        {
            return new LocateResult(null, "container_not_found");
        }

        if (candidates.Length != 1)
        {
            return new LocateResult(null, "identity_mismatch");
        }

        BytesResult inspectResponse = await GetBytesAsync(
            $"/containers/{candidates[0]}/json",
            cancellationToken);
        if (inspectResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return new LocateResult(null, "container_not_found");
        }

        JsonDocument? inspect = ParseJson(inspectResponse.Bytes);
        if (inspect is null)
        {
            return new LocateResult(null, inspectResponse.Outcome);
        }

        using (inspect)
        {
            JsonElement root = inspect.RootElement;
            bool valid = TryGetString(root, "Id", out string inspectedId)
                && string.Equals(inspectedId, candidates[0], StringComparison.Ordinal)
                && TryGetString(root, "Name", out string inspectedName)
                && string.Equals(inspectedName, $"/{bot.ContainerName}", StringComparison.Ordinal)
                && root.TryGetProperty("Config", out JsonElement config)
                && config.TryGetProperty("Labels", out JsonElement labels)
                && HasExpectedLabels(labels, bot);
            return valid
                ? new LocateResult(candidates[0], "succeeded")
                : new LocateResult(null, "identity_mismatch");
        }
    }

    private async Task<BytesResult> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new BytesResult(null, response.StatusCode, "container_not_found");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new BytesResult(null, response.StatusCode, "docker_error");
            }

            if (response.Content.Headers.ContentLength > maximumResponseBytes)
            {
                return new BytesResult(null, response.StatusCode, "response_too_large");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int read = await source.ReadAsync(chunk, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maximumResponseBytes)
                {
                    return new BytesResult(null, response.StatusCode, "response_too_large");
                }

                buffer.Write(chunk, 0, read);
            }

            return new BytesResult(buffer.ToArray(), response.StatusCode, "succeeded");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BytesResult(null, null, "docker_timeout");
        }
        catch (HttpRequestException)
        {
            return new BytesResult(null, null, "docker_unavailable");
        }
        catch (IOException)
        {
            return new BytesResult(null, null, "docker_unavailable");
        }
    }

    private IEnumerable<string> ReadCandidates(JsonElement root, ManagedBot bot)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement container in root.EnumerateArray())
        {
            if (TryGetString(container, "Id", out string containerId)
                && IsValidContainerId(containerId)
                && !IsSelf(containerId)
                && container.TryGetProperty("Labels", out JsonElement labels)
                && HasExpectedLabels(labels, bot)
                && HasExpectedName(container, bot.ContainerName))
            {
                yield return containerId;
            }
        }
    }

    private static byte[] ExtractDockerPayload(byte[] response)
    {
        if (response.Length < 8 || response[0] is not 1 and not 2
            || response[1] != 0 || response[2] != 0 || response[3] != 0)
        {
            return response;
        }

        using var payload = new MemoryStream();
        int offset = 0;
        while (offset < response.Length)
        {
            if (response.Length - offset < 8
                || response[offset] is not 1 and not 2
                || response[offset + 1] != 0
                || response[offset + 2] != 0
                || response[offset + 3] != 0)
            {
                return response;
            }

            int length = BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(offset + 4, 4));
            offset += 8;
            if (length < 0 || length > response.Length - offset)
            {
                return response;
            }

            payload.Write(response, offset, length);
            offset += length;
        }

        return payload.ToArray();
    }

    private static JsonDocument? ParseJson(byte[]? bytes)
    {
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeBot(ManagedBot bot) =>
        BotIdValidator.IsValid(bot.Id)
        && !ReservedIdentities.Contains(bot.ComposeServiceName)
        && !ReservedIdentities.Contains(bot.ContainerName)
        && IsSafeIdentity(bot.ComposeServiceName)
        && IsSafeIdentity(bot.ContainerName);

    private static bool IsSafeIdentity(string value) =>
        !string.IsNullOrEmpty(value)
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static bool HasExpectedLabels(JsonElement labels, ManagedBot bot) =>
        HasExactLabel(labels, "com.gymbeam.managed", "true")
        && HasExactLabel(labels, "com.gymbeam.bot-id", bot.Id)
        && HasExactLabel(labels, "com.gymbeam.role", "bot")
        && HasExactLabel(labels, "com.docker.compose.service", bot.ComposeServiceName);

    private static bool HasExpectedName(JsonElement container, string expectedName) =>
        container.TryGetProperty("Names", out JsonElement names)
        && names.ValueKind == JsonValueKind.Array
        && names.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String
            && string.Equals(name.GetString(), $"/{expectedName}", StringComparison.Ordinal));

    private static bool HasExactLabel(JsonElement labels, string name, string expected) =>
        TryGetString(labels, name, out string value)
        && string.Equals(value, expected, StringComparison.Ordinal);

    private bool IsSelf(string containerId) =>
        !string.IsNullOrWhiteSpace(selfContainerId)
        && (containerId.StartsWith(selfContainerId, StringComparison.Ordinal)
            || selfContainerId.StartsWith(containerId, StringComparison.Ordinal));

    private static bool IsValidContainerId(string value) =>
        value.Length is >= 12 and <= 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out JsonElement child)
            || child.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = child.GetString() ?? string.Empty;
        return true;
    }

    private sealed record LocateResult(string? ContainerId, string Outcome);
    private sealed record BytesResult(byte[]? Bytes, HttpStatusCode? StatusCode, string Outcome);
}
