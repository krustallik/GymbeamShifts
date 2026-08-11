using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GymBeam.AdminManager.Provisioning;

public sealed class SafeDockerProvisioner(
    HttpClient httpClient,
    TimeSpan requestTimeout,
    string image,
    string network,
    string hostInstancesPath,
    string? selfContainerId)
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const long RequiredMemoryBytes = 512L * 1024 * 1024;
    private static readonly string ManagedListPath =
        "/containers/json?all=true&filters=%7B%22label%22%3A%5B%22com.gymbeam.managed%3Dtrue%22%5D%7D";

    public async Task<ResourceResult> CheckCapacityAsync(CancellationToken cancellationToken = default)
    {
        DockerResponse response = await SendAsync(HttpMethod.Get, "/info", null, cancellationToken);
        if (response.Document is null)
        {
            return ResourceResult.Failure(response.Outcome);
        }

        using (response.Document)
        {
            return response.Document.RootElement.TryGetProperty("MemTotal", out JsonElement memory)
                && memory.TryGetInt64(out long available)
                && available >= RequiredMemoryBytes
                    ? ResourceResult.Success()
                    : ResourceResult.Failure("insufficient_ram");
        }
    }

    public async Task<ResourceResult> CreateAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!SafeInstanceProvisioner.HasDerivedIdentity(spec))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        string hostPath = hostInstancesPath.TrimEnd('/', '\\') + "/" + spec.BotId;
        var payload = new
        {
            Image = image,
            Labels = ExpectedLabels(spec),
            Env = new[]
            {
                "GYMBEAM_ADMIN_HOST=*",
                "GYMBEAM_ADMIN_PORT=8080",
                "CHROME_BIN=/usr/bin/chromium",
                "GYMBEAM_LOG_PATH=/app/runtime-data/app.log",
                "GYMBEAM_ENV_PATH=/app/instance/.env"
            },
            ExposedPorts = new Dictionary<string, object> { ["8080/tcp"] = new { } },
            Healthcheck = new
            {
                Test = new[] { "CMD-SHELL", "curl --fail --silent --show-error http://localhost:8080/healthz > /dev/null || exit 1" },
                Interval = 30_000_000_000L,
                Timeout = 5_000_000_000L,
                Retries = 3,
                StartPeriod = 60_000_000_000L
            },
            HostConfig = new
            {
                Binds = new[]
                {
                    $"{hostPath}:/app/instance:ro",
                    $"{hostPath}/.env:/app/instance/.env",
                    $"{hostPath}/appconfig.json:/app/appconfig.json",
                    $"{hostPath}/runtime-data:/app/runtime-data"
                },
                Memory = RequiredMemoryBytes,
                ShmSize = 256L * 1024 * 1024,
                NetworkMode = network,
                Init = true,
                LogConfig = new
                {
                    Type = "json-file",
                    Config = new Dictionary<string, string>
                    {
                        ["max-size"] = "10m",
                        ["max-file"] = "3"
                    }
                },
                RestartPolicy = new { Name = "unless-stopped", MaximumRetryCount = 0 }
            }
        };
        DockerResponse created = await SendAsync(
            HttpMethod.Post,
            $"/containers/create?name={Uri.EscapeDataString(spec.ContainerName)}",
            JsonContent.Create(payload, options: new JsonSerializerOptions()),
            cancellationToken);
        if (created.StatusCode != HttpStatusCode.Created || created.Document is null)
        {
            created.Document?.Dispose();
            return ResourceResult.Failure(created.StatusCode == HttpStatusCode.Conflict
                ? "container_exists"
                : created.Outcome);
        }

        string? containerId;
        using (created.Document)
        {
            containerId = created.Document.RootElement.TryGetProperty("Id", out JsonElement id)
                ? id.GetString()
                : null;
        }

        if (!IsContainerId(containerId))
        {
            return ResourceResult.Failure("invalid_response");
        }

        DockerResponse started = await SendAsync(
            HttpMethod.Post,
            $"/containers/{containerId}/start",
            null,
            cancellationToken);
        started.Document?.Dispose();
        return started.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified
            ? ResourceResult.Success()
            : ResourceResult.Failure(started.Outcome);
    }

    public Task<ResourceResult> WaitHealthyAsync(
        ProvisioningSpec spec,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken = default) =>
        WaitHealthyCoreAsync(spec, timeout, pollInterval, cancellationToken);

    public async Task<ResourceResult> RemoveAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        if (!SafeInstanceProvisioner.HasManagedIdentity(spec))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        DockerResponse listed = await SendAsync(HttpMethod.Get, ManagedListPath, null, cancellationToken);
        if (listed.Document is null)
        {
            return ResourceResult.Failure(listed.Outcome);
        }

        List<string> candidates;
        bool claimedIdentity;
        using (listed.Document)
        {
            JsonElement root = listed.Document.RootElement;
            candidates = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().Where(item => HasExpectedIdentity(item, spec)).Select(item => item.GetProperty("Id").GetString()!).ToList()
                : [];
            claimedIdentity = root.ValueKind == JsonValueKind.Array
                && root.EnumerateArray().Any(item => ClaimsIdentity(item, spec));
        }

        if (candidates.Count == 0)
        {
            return claimedIdentity
                ? ResourceResult.Failure("identity_mismatch")
                : ResourceResult.Success();
        }

        if (candidates.Count != 1 || IsSelf(candidates[0]))
        {
            return ResourceResult.Failure("identity_mismatch");
        }

        DockerResponse inspected = await SendAsync(
            HttpMethod.Get, $"/containers/{candidates[0]}/json", null, cancellationToken);
        if (inspected.StatusCode == HttpStatusCode.NotFound)
        {
            inspected.Document?.Dispose();
            return ResourceResult.Success();
        }

        if (inspected.Document is null)
        {
            return ResourceResult.Failure(inspected.Outcome);
        }

        using (inspected.Document)
        {
            if (!HasExpectedInspectIdentity(inspected.Document.RootElement, candidates[0], spec))
            {
                return ResourceResult.Failure("identity_mismatch");
            }
        }

        DockerResponse removed = await SendAsync(
            HttpMethod.Delete, $"/containers/{candidates[0]}?force=true&v=true", null, cancellationToken);
        removed.Document?.Dispose();
        return removed.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound
            ? ResourceResult.Success()
            : ResourceResult.Failure(removed.Outcome);
    }

    private async Task<ResourceResult> WaitHealthyCoreAsync(
        ProvisioningSpec spec,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(startedAt) < timeout)
        {
            DockerResponse listed = await SendAsync(HttpMethod.Get, ManagedListPath, null, cancellationToken);
            if (listed.Document is null)
            {
                return ResourceResult.Failure(listed.Outcome);
            }

            string? id;
            using (listed.Document)
            {
                List<string> ids = listed.Document.RootElement.ValueKind == JsonValueKind.Array
                    ? listed.Document.RootElement.EnumerateArray().Where(item => HasExpectedIdentity(item, spec)).Select(item => item.GetProperty("Id").GetString()!).ToList()
                    : [];
                if (ids.Count != 1)
                {
                    return ResourceResult.Failure(ids.Count == 0 ? "container_not_found" : "identity_mismatch");
                }

                id = ids[0];
            }

            DockerResponse inspect = await SendAsync(HttpMethod.Get, $"/containers/{id}/json", null, cancellationToken);
            if (inspect.Document is not null)
            {
                using (inspect.Document)
                {
                    JsonElement root = inspect.Document.RootElement;
                    if (!HasExpectedInspectIdentity(root, id, spec))
                    {
                        return ResourceResult.Failure("identity_mismatch");
                    }

                    if (root.TryGetProperty("State", out JsonElement state)
                        && state.TryGetProperty("Status", out JsonElement status)
                        && status.GetString() == "running"
                        && state.TryGetProperty("Health", out JsonElement health)
                        && health.TryGetProperty("Status", out JsonElement healthStatus)
                        && healthStatus.GetString() == "healthy")
                    {
                        return ResourceResult.Success();
                    }
                }
            }

            await Task.Delay(pollInterval, cancellationToken);
        }

        return ResourceResult.Failure("health_timeout");
    }

    private async Task<DockerResponse> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            using HttpResponseMessage response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            JsonDocument? document = null;
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            {
                return new DockerResponse(null, response.StatusCode, "invalid_response");
            }

            if (response.Content.Headers.ContentLength != 0)
            {
                byte[] body = await ReadBoundedAsync(response.Content, timeout.Token);
                if (body.Length > 0)
                {
                    try
                    {
                        document = JsonDocument.Parse(body);
                    }
                    catch (JsonException)
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            return new DockerResponse(null, response.StatusCode, "invalid_response");
                        }
                    }
                }
            }

            return new DockerResponse(document, response.StatusCode,
                response.IsSuccessStatusCode ? "ok" : "docker_error");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DockerResponse(null, null, "docker_timeout");
        }
        catch (HttpRequestException)
        {
            return new DockerResponse(null, null, "docker_unavailable");
        }
        catch (InvalidDataException)
        {
            return new DockerResponse(null, null, "invalid_response");
        }
        catch (IOException)
        {
            return new DockerResponse(null, null, "docker_unavailable");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException("Docker response exceeds the configured limit.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static Dictionary<string, string> ExpectedLabels(ProvisioningSpec spec) => new(StringComparer.Ordinal)
    {
        ["com.gymbeam.managed"] = "true",
        ["com.gymbeam.bot-id"] = spec.BotId,
        ["com.gymbeam.role"] = "bot",
        ["com.docker.compose.service"] = spec.ComposeServiceName
    };

    private static bool HasExpectedIdentity(JsonElement item, ProvisioningSpec spec) =>
        item.TryGetProperty("Id", out JsonElement id)
        && IsContainerId(id.GetString())
        && item.TryGetProperty("Names", out JsonElement names)
        && names.ValueKind == JsonValueKind.Array
        && names.EnumerateArray().Any(name => name.GetString() == $"/{spec.ContainerName}")
        && item.TryGetProperty("Labels", out JsonElement labels)
        && ExpectedLabels(spec).All(expected => labels.TryGetProperty(expected.Key, out JsonElement value)
            && value.GetString() == expected.Value);

    private static bool ClaimsIdentity(JsonElement item, ProvisioningSpec spec) =>
        (item.TryGetProperty("Names", out JsonElement names)
            && names.ValueKind == JsonValueKind.Array
            && names.EnumerateArray().Any(name => name.GetString() == $"/{spec.ContainerName}"))
        || (item.TryGetProperty("Labels", out JsonElement labels)
            && ((labels.TryGetProperty("com.gymbeam.bot-id", out JsonElement botId)
                    && botId.GetString() == spec.BotId)
                || (labels.TryGetProperty("com.docker.compose.service", out JsonElement service)
                    && service.GetString() == spec.ComposeServiceName)));

    private static bool HasExpectedInspectIdentity(JsonElement root, string id, ProvisioningSpec spec) =>
        root.TryGetProperty("Id", out JsonElement inspectedId)
        && inspectedId.GetString() == id
        && root.TryGetProperty("Name", out JsonElement name)
        && name.GetString() == $"/{spec.ContainerName}"
        && root.TryGetProperty("Config", out JsonElement config)
        && config.TryGetProperty("Labels", out JsonElement labels)
        && ExpectedLabels(spec).All(expected => labels.TryGetProperty(expected.Key, out JsonElement value)
            && value.GetString() == expected.Value);

    private bool IsSelf(string id) => !string.IsNullOrWhiteSpace(selfContainerId)
        && (id.StartsWith(selfContainerId, StringComparison.Ordinal)
            || selfContainerId.StartsWith(id, StringComparison.Ordinal));

    private static bool IsContainerId(string? id) => id is { Length: >= 12 and <= 64 }
        && id.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record DockerResponse(JsonDocument? Document, HttpStatusCode? StatusCode, string Outcome);
}
