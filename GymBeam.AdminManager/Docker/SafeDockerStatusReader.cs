using System.Net;
using System.Text.Json;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Docker;

public sealed class SafeDockerStatusReader(
    HttpClient httpClient,
    TimeProvider timeProvider,
    TimeSpan requestTimeout,
    string? selfContainerId) : IDockerStatusReader
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumConcurrentInspects = 8;
    private const string ListPath = "/containers/json?all=true&filters=%7B%22label%22%3A%5B%22com.gymbeam.managed%3Dtrue%22%5D%7D";
    private static readonly HashSet<string> ReservedIdentities = new(StringComparer.Ordinal)
    {
        "caddy",
        "gymbeam-caddy",
        "gymbeam-admin-manager"
    };

    public async Task<IReadOnlyDictionary<string, BotRuntimeStatus>> GetStatusesAsync(
        IReadOnlyList<ManagedBot> bots,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, BotRuntimeStatus>(StringComparer.Ordinal);
        foreach (ManagedBot bot in bots)
        {
            result.TryAdd(bot.Id, BotRuntimeStatus.Unknown());
        }

        Dictionary<string, ManagedBot> eligibleBots = GetEligibleBots(bots);
        if (eligibleBots.Count == 0)
        {
            return result;
        }

        JsonDocument? listDocument = await TryGetJsonAsync(ListPath, cancellationToken);
        if (listDocument is null)
        {
            return result;
        }

        using (listDocument)
        {
            Dictionary<string, List<string>> candidates = ReadCandidates(listDocument.RootElement, eligibleBots);
            using var inspectGate = new SemaphoreSlim(MaximumConcurrentInspects);
            Task<(string BotId, BotRuntimeStatus Status)>[] inspections = candidates
                .Where(candidate => candidate.Value.Count == 1)
                .Select(async candidate =>
                {
                    await inspectGate.WaitAsync(cancellationToken);
                    try
                    {
                        ManagedBot bot = eligibleBots[candidate.Key];
                        BotRuntimeStatus status = await InspectAsync(bot, candidate.Value[0], cancellationToken);
                        return (bot.Id, status);
                    }
                    finally
                    {
                        inspectGate.Release();
                    }
                })
                .ToArray();

            foreach ((string botId, BotRuntimeStatus status) in await Task.WhenAll(inspections))
            {
                result[botId] = status;
            }
        }

        return result;
    }

    private static Dictionary<string, ManagedBot> GetEligibleBots(IReadOnlyList<ManagedBot> bots)
    {
        var duplicateIds = bots
            .GroupBy(bot => bot.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        return bots
            .Where(bot => !duplicateIds.Contains(bot.Id)
                && BotIdValidator.IsValid(bot.Id)
                && IsSafeExpectedIdentity(bot))
            .ToDictionary(bot => bot.Id, StringComparer.Ordinal);
    }

    private static bool IsSafeExpectedIdentity(ManagedBot bot)
    {
        return !string.IsNullOrWhiteSpace(bot.ComposeServiceName)
            && !string.IsNullOrWhiteSpace(bot.ContainerName)
            && !ReservedIdentities.Contains(bot.ComposeServiceName)
            && !ReservedIdentities.Contains(bot.ContainerName)
            && bot.ComposeServiceName.All(IsSafeIdentityCharacter)
            && bot.ContainerName.All(IsSafeIdentityCharacter);
    }

    private static bool IsSafeIdentityCharacter(char character)
    {
        return character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
    }

    private Dictionary<string, List<string>> ReadCandidates(
        JsonElement root,
        IReadOnlyDictionary<string, ManagedBot> eligibleBots)
    {
        var candidates = eligibleBots.Keys.ToDictionary(
            botId => botId,
            _ => new List<string>(),
            StringComparer.Ordinal);

        if (root.ValueKind != JsonValueKind.Array)
        {
            return candidates;
        }

        foreach (JsonElement container in root.EnumerateArray())
        {
            if (!TryGetString(container, "Id", out string? containerId)
                || !IsValidContainerId(containerId)
                || IsSelf(containerId)
                || !container.TryGetProperty("Labels", out JsonElement labels)
                || !TryGetString(labels, "com.gymbeam.bot-id", out string? botId)
                || !eligibleBots.TryGetValue(botId, out ManagedBot? bot)
                || !HasExpectedLabels(labels, bot)
                || !HasExpectedName(container, bot.ContainerName))
            {
                continue;
            }

            candidates[bot.Id].Add(containerId);
        }

        return candidates;
    }

    private async Task<BotRuntimeStatus> InspectAsync(
        ManagedBot bot,
        string containerId,
        CancellationToken cancellationToken)
    {
        JsonDocument? inspectDocument = await TryGetJsonAsync(
            $"/containers/{containerId}/json",
            cancellationToken);
        if (inspectDocument is null)
        {
            return BotRuntimeStatus.Unknown();
        }

        using (inspectDocument)
        {
            JsonElement root = inspectDocument.RootElement;
            if (!TryGetString(root, "Id", out string? inspectedId)
                || !string.Equals(inspectedId, containerId, StringComparison.Ordinal)
                || !TryGetString(root, "Name", out string? inspectedName)
                || !string.Equals(inspectedName, $"/{bot.ContainerName}", StringComparison.Ordinal)
                || !root.TryGetProperty("Config", out JsonElement config)
                || !config.TryGetProperty("Labels", out JsonElement labels)
                || !HasExpectedLabels(labels, bot)
                || !root.TryGetProperty("State", out JsonElement stateElement))
            {
                return BotRuntimeStatus.Unknown();
            }

            string state = ReadKnownValue(stateElement, "Status",
                ["created", "running", "paused", "restarting", "removing", "exited", "dead"]);
            string health = stateElement.TryGetProperty("Health", out JsonElement healthElement)
                ? ReadKnownValue(healthElement, "Status", ["starting", "healthy", "unhealthy"])
                : "unknown";
            DateTimeOffset? created = ReadTimestamp(root, "Created");
            DateTimeOffset? started = ReadTimestamp(stateElement, "StartedAt");
            DateTimeOffset? finished = ReadTimestamp(stateElement, "FinishedAt");
            TimeSpan? uptime = CalculateUptime(state, started, finished);
            DateTimeOffset? lastUpdated = state == "running" ? started : finished ?? started ?? created;

            return new BotRuntimeStatus(state, health, uptime, lastUpdated, "available");
        }
    }

    private TimeSpan? CalculateUptime(
        string state,
        DateTimeOffset? started,
        DateTimeOffset? finished)
    {
        if (started is null)
        {
            return null;
        }

        DateTimeOffset end = state == "running" ? timeProvider.GetUtcNow() : finished ?? started.Value;
        return end >= started.Value ? end - started.Value : null;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(requestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            if (response.StatusCode is not HttpStatusCode.OK)
            {
                return null;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(timeoutSource.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            while (true)
            {
                int bytesRead = await stream.ReadAsync(chunk, timeoutSource.Token);
                if (bytesRead == 0)
                {
                    break;
                }

                if (buffer.Length + bytesRead > MaximumResponseBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, bytesRead);
            }

            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasExpectedLabels(JsonElement labels, ManagedBot bot)
    {
        return HasExactLabel(labels, "com.gymbeam.managed", "true")
            && HasExactLabel(labels, "com.gymbeam.bot-id", bot.Id)
            && HasExactLabel(labels, "com.gymbeam.role", "bot")
            && HasExactLabel(labels, "com.docker.compose.service", bot.ComposeServiceName);
    }

    private static bool HasExpectedName(JsonElement container, string expectedName)
    {
        if (!container.TryGetProperty("Names", out JsonElement names)
            || names.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return names.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String
            && string.Equals(name.GetString(), $"/{expectedName}", StringComparison.Ordinal));
    }

    private static bool HasExactLabel(JsonElement labels, string name, string expected)
    {
        return TryGetString(labels, name, out string? value)
            && string.Equals(value, expected, StringComparison.Ordinal);
    }

    private bool IsSelf(string containerId)
    {
        return !string.IsNullOrWhiteSpace(selfContainerId)
            && (containerId.StartsWith(selfContainerId, StringComparison.Ordinal)
                || selfContainerId.StartsWith(containerId, StringComparison.Ordinal));
    }

    private static bool IsValidContainerId(string value)
    {
        return value.Length is >= 12 and <= 64
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static string ReadKnownValue(JsonElement element, string property, string[] knownValues)
    {
        return TryGetString(element, property, out string? value)
            && knownValues.Contains(value, StringComparer.Ordinal)
                ? value
                : "unknown";
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        return TryGetString(element, property, out string? value)
            && DateTimeOffset.TryParse(value, out DateTimeOffset timestamp)
                ? timestamp.ToUniversalTime()
                : null;
    }

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
}
