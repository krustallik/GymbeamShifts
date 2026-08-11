using GymBeam.AdminManager.Auditing;
using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Logs;

public sealed record BotLogView(string BotId, int Tail, string Outcome, string Logs);

public sealed class BotLogService(
    IManagedBotRegistry registry,
    IDockerLogReader docker,
    AuditLogger audit)
{
    private const int MaximumReturnedCharacters = 256 * 1024;

    public async Task<BotLogView> ReadAsync(
        string botId,
        int tail,
        string actor,
        string remoteAddress,
        CancellationToken cancellationToken = default)
    {
        if (!BotIdValidator.IsValid(botId) || tail is < 1 or > 500)
        {
            await AuditAsync(
                "invalid_request",
                actor,
                BotIdValidator.IsValid(botId) ? botId : null,
                remoteAddress);
            return new BotLogView(botId, tail, "invalid_request", string.Empty);
        }

        ManagedBot[] matches = (await registry.GetAllAsync(cancellationToken))
            .Where(bot => string.Equals(bot.Id, botId, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            await AuditAsync("bot_not_found", actor, botId, remoteAddress);
            return new BotLogView(botId, tail, "bot_not_found", string.Empty);
        }

        ManagedBot bot = matches[0];
        DockerLogResult result = await docker.ReadAsync(bot, tail, cancellationToken);
        string applicationLogs = ReadApplicationLogs(bot, tail);
        string logs = !string.IsNullOrWhiteSpace(applicationLogs) ? applicationLogs : result.Logs;
        string outcome = result.Outcome == "succeeded" || !string.IsNullOrWhiteSpace(applicationLogs)
            ? "succeeded"
            : result.Outcome;
        await AuditAsync(outcome, actor, botId, remoteAddress);
        return new BotLogView(botId, tail, outcome, logs);
    }

    private static string ReadApplicationLogs(ManagedBot bot, int tail)
    {
        try
        {
            string instancePath = Path.GetFullPath(bot.InstancePath);
            if (!Directory.Exists(instancePath)
                || File.GetAttributes(instancePath).HasFlag(FileAttributes.ReparsePoint))
            {
                return string.Empty;
            }

            string runtimePath = Path.GetFullPath(Path.Combine(instancePath, "runtime-data"));
            if (!string.Equals(Path.GetDirectoryName(runtimePath), instancePath, PathComparison())
                || !Directory.Exists(runtimePath)
                || File.GetAttributes(runtimePath).HasFlag(FileAttributes.ReparsePoint))
            {
                return string.Empty;
            }

            string logPath = Path.GetFullPath(Path.Combine(runtimePath, "app.log"));
            if (!string.Equals(Path.GetDirectoryName(logPath), runtimePath, PathComparison())
                || !File.Exists(logPath)
                || File.GetAttributes(logPath).HasFlag(FileAttributes.ReparsePoint))
            {
                return string.Empty;
            }

            string joined = string.Join(Environment.NewLine, File.ReadLines(logPath).TakeLast(tail));
            if (joined.Length > MaximumReturnedCharacters) joined = joined[^MaximumReturnedCharacters..];
            return SafeDockerLogReader.TryRedact(joined, out string redacted) ? redacted : string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private Task AuditAsync(string outcome, string actor, string? target, string remoteAddress) =>
        audit.WriteAsync(
            "bot.logs.view",
            outcome,
            actor,
            target,
            remoteAddress,
            CancellationToken.None);
}
