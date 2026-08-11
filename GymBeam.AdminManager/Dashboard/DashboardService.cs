using GymBeam.AdminManager.Docker;
using GymBeam.AdminManager.Registry;

namespace GymBeam.AdminManager.Dashboard;

public sealed class DashboardService(
    IManagedBotRegistry registry,
    IDockerStatusReader dockerStatusReader)
{
    public async Task<IReadOnlyList<BotDashboardItem>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ManagedBot> bots = await registry.GetAllAsync(cancellationToken);
        IReadOnlyDictionary<string, BotRuntimeStatus> statuses;
        try
        {
            statuses = await dockerStatusReader.GetStatusesAsync(bots, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            statuses = new Dictionary<string, BotRuntimeStatus>(StringComparer.OrdinalIgnoreCase);
        }

        return bots.Select(bot =>
        {
            BotRuntimeStatus status = statuses.TryGetValue(bot.Id, out BotRuntimeStatus? found)
                ? found
                : BotRuntimeStatus.Unknown("container-not-found");
            return new BotDashboardItem(
                bot.Id,
                bot.DisplayName,
                bot.Enabled,
                status.State,
                status.Health,
                status.Uptime,
                status.LastUpdatedAtUtc,
                status.Availability,
                GetDirectorySize(bot.InstancePath));
        }).ToArray();
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            return Directory.EnumerateFiles(path, "*", options)
                .Sum(file => new FileInfo(file).Length);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
