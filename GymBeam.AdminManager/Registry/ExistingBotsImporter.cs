using GymBeam.AdminManager.Auditing;

namespace GymBeam.AdminManager.Registry;

public sealed class ExistingBotsImporter(
    string instancesPath,
    string baseDomain,
    PersistentBotRegistry registry,
    AuditLogger audit)
{
    private static readonly BotTemplate[] Templates =
    [
        new("bot1", "Bot 1", "gymbeam-bot-1", "gymbeam-shifts-bot-1"),
        new("bot2", "Bot 2", "gymbeam-bot-2", "gymbeam-shifts-bot-2")
    ];

    public async Task<IReadOnlyList<ManagedBot>> ImportAsync(CancellationToken cancellationToken = default)
    {
        var imported = new List<ManagedBot>();
        foreach (BotTemplate template in Templates)
        {
            string botPath = Path.Combine(instancesPath, template.Id);
            if (!Directory.Exists(botPath)
                || !File.Exists(Path.Combine(botPath, ".env"))
                || !File.Exists(Path.Combine(botPath, "appconfig.json")))
            {
                continue;
            }

            var descriptor = new ExistingBotDescriptor(
                template.Id,
                template.DisplayName,
                template.ComposeServiceName,
                template.ContainerName,
                botPath);
            ManagedBot? bot = await registry.TryImportOrMigrateAsync(
                descriptor,
                $"{template.Id}.{baseDomain}",
                cancellationToken);
            if (bot is null)
            {
                continue;
            }

            imported.Add(bot);
            await audit.WriteAsync(
                "bot.import",
                "success",
                actor: "system",
                target: bot.Id,
                remoteAddress: null,
                cancellationToken);
        }

        return imported;
    }

    private sealed record BotTemplate(
        string Id,
        string DisplayName,
        string ComposeServiceName,
        string ContainerName);
}
