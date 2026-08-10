using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public sealed class SafeDockerLifecycleController(
    HttpClient httpClient,
    TimeSpan requestTimeout,
    TimeSpan healthTimeout,
    TimeSpan healthPollInterval,
    string? selfContainerId) : IDockerLifecycleController
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const string ListPath = "/containers/json?all=true&filters=%7B%22label%22%3A%5B%22com.gymbeam.managed%3Dtrue%22%5D%7D";
    private static readonly HashSet<string> ReservedIdentities = new(StringComparer.Ordinal)
    {
        "caddy",
        "gymbeam-caddy",
        "gymbeam-admin-manager"
    };

    public async Task<DockerLifecycleResult> ExecuteAsync(
        ManagedBot bot,
        BotLifecycleAction action,
        CancellationToken cancellationToken = default)
    {
        if (!IsSafeBot(bot))
        {
            return Result("identity_mismatch", "Managed bot identity is invalid");
        }

        ResolveResult resolved = await ResolveAndInspectAsync(bot, cancellationToken);
        if (resolved.Container is null)
        {
            return Result(resolved.Outcome, resolved.Message);
        }

        if (action == BotLifecycleAction.Start && resolved.Container.State == "running")
        {
            return Result("already_running", "Container is already running");
        }

        if (action == BotLifecycleAction.Stop
            && resolved.Container.State is "created" or "exited" or "dead")
        {
            return Result("already_stopped", "Container is already stopped");
        }

        string operationPath = action switch
        {
            BotLifecycleAction.Start => $"/containers/{resolved.Container.Id}/start",
            BotLifecycleAction.Stop => $"/containers/{resolved.Container.Id}/stop?t=10",
            BotLifecycleAction.Restart => $"/containers/{resolved.Container.Id}/restart?t=10",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        HttpResult operation = await SendAsync(HttpMethod.Post, operationPath, cancellationToken);
        if (operation.StatusCode == HttpStatusCode.NotFound)
        {
            return Result("container_not_found", "Container disappeared during operation");
        }

        if (operation.Outcome != "ok"
            || operation.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
        {
            return Result(operation.Outcome, "Docker lifecycle operation failed");
        }

        if (action == BotLifecycleAction.Stop)
        {
            return Result("succeeded", "Container stopped");
        }

        return await WaitForHealthyAsync(bot, resolved.Container.Id, cancellationToken);
    }

    private async Task<DockerLifecycleResult> WaitForHealthyAsync(
        ManagedBot bot,
        string containerId,
        CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < healthTimeout)
        {
            InspectResult inspected = await InspectAsync(bot, containerId, cancellationToken);
            if (inspected.Outcome == "container_not_found")
            {
                return Result("container_not_found", "Container disappeared during health check");
            }

            if (inspected.Outcome == "identity_mismatch")
            {
                return Result("identity_mismatch", "Container identity changed during health check");
            }

            if (inspected.Container is not null
                && inspected.Container.State == "running"
                && inspected.Container.Health == "healthy")
            {
                return Result("succeeded", "Container is running and healthy");
            }

            await Task.Delay(healthPollInterval, cancellationToken);
        }

        return Result("health_timeout", "Container did not become healthy before timeout");
    }

    private async Task<ResolveResult> ResolveAndInspectAsync(
        ManagedBot bot,
        CancellationToken cancellationToken)
    {
        HttpResult listResult = await SendAsync(HttpMethod.Get, ListPath, cancellationToken);
        if (listResult.Document is null)
        {
            return new ResolveResult(null, listResult.Outcome, "Docker is unavailable");
        }

        using (listResult.Document)
        {
            List<string> candidates = ReadCandidates(listResult.Document.RootElement, bot);
            if (candidates.Count == 0)
            {
                return new ResolveResult(null, "container_not_found", "Managed container was not found");
            }

            if (candidates.Count != 1)
            {
                return new ResolveResult(null, "identity_mismatch", "Managed container identity is ambiguous");
            }

            InspectResult inspected = await InspectAsync(bot, candidates[0], cancellationToken);
            return new ResolveResult(inspected.Container, inspected.Outcome, inspected.Message);
        }
    }

    private async Task<InspectResult> InspectAsync(
        ManagedBot bot,
        string containerId,
        CancellationToken cancellationToken)
    {
        HttpResult result = await SendAsync(
            HttpMethod.Get,
            $"/containers/{containerId}/json",
            cancellationToken);
        if (result.StatusCode == HttpStatusCode.NotFound)
        {
            return new InspectResult(null, "container_not_found", "Managed container was not found");
        }

        if (result.Document is null)
        {
            return new InspectResult(null, result.Outcome, "Docker inspect failed");
        }

        using (result.Document)
        {
            JsonElement root = result.Document.RootElement;
            if (!TryGetString(root, "Id", out string inspectedId)
                || !string.Equals(inspectedId, containerId, StringComparison.Ordinal)
                || !TryGetString(root, "Name", out string inspectedName)
                || !string.Equals(inspectedName, $"/{bot.ContainerName}", StringComparison.Ordinal)
                || !root.TryGetProperty("Config", out JsonElement config)
                || !config.TryGetProperty("Labels", out JsonElement labels)
                || !HasExpectedLabels(labels, bot)
                || !root.TryGetProperty("State", out JsonElement state))
            {
                return new InspectResult(null, "identity_mismatch", "Container identity validation failed");
            }

            string stateValue = TryGetString(state, "Status", out string parsedState)
                ? parsedState
                : "unknown";
            string health = state.TryGetProperty("Health", out JsonElement healthElement)
                && TryGetString(healthElement, "Status", out string parsedHealth)
                    ? parsedHealth
                    : "unknown";
            return new InspectResult(
                new InspectedContainer(containerId, stateValue, health),
                "ok",
                "Container identity validated");
        }
    }

    private List<string> ReadCandidates(JsonElement root, ManagedBot bot)
    {
        var candidates = new List<string>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return candidates;
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
                candidates.Add(containerId);
            }
        }

        return candidates;
    }

    private async Task<HttpResult> SendAsync(
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(method, path);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            if (method != HttpMethod.Get)
            {
                return new HttpResult(null, response.StatusCode, "ok");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new HttpResult(null, response.StatusCode, "docker_error");
            }

            JsonDocument? document = await ReadJsonAsync(response.Content, timeout.Token);
            return document is null
                ? new HttpResult(null, response.StatusCode, "invalid_response")
                : new HttpResult(document, response.StatusCode, "ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HttpResult(null, null, "docker_timeout");
        }
        catch (HttpRequestException)
        {
            return new HttpResult(null, null, "docker_unavailable");
        }
        catch (IOException)
        {
            return new HttpResult(null, null, "docker_unavailable");
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            return null;
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[8192];
        while (true)
        {
            int read = await source.ReadAsync(bytes, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                return null;
            }

            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSafeBot(ManagedBot bot)
    {
        return BotIdValidator.IsValid(bot.Id)
            && !ReservedIdentities.Contains(bot.ComposeServiceName)
            && !ReservedIdentities.Contains(bot.ContainerName)
            && IsSafeIdentity(bot.ComposeServiceName)
            && IsSafeIdentity(bot.ContainerName);
    }

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

    private static DockerLifecycleResult Result(string outcome, string message) => new(outcome, message);

    private sealed record InspectedContainer(string Id, string State, string Health);
    private sealed record ResolveResult(InspectedContainer? Container, string Outcome, string Message);
    private sealed record InspectResult(InspectedContainer? Container, string Outcome, string Message);
    private sealed record HttpResult(JsonDocument? Document, HttpStatusCode? StatusCode, string Outcome);
}
