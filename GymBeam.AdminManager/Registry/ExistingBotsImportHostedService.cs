using Microsoft.Extensions.Hosting;

namespace GymBeam.AdminManager.Registry;

internal sealed class ExistingBotsImportHostedService(ExistingBotsImporter importer) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await importer.ImportAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
