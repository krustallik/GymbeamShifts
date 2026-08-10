using System.Net;

namespace GymBeam.AdminManager.Provisioning;

public sealed class ProvisioningResources(
    SafeInstanceProvisioner instances,
    SafeDockerProvisioner docker,
    SafeCaddyRouteManager caddy,
    HttpClient externalHealthClient,
    string instancesRoot,
    long minimumFreeDiskBytes,
    TimeSpan healthTimeout,
    TimeSpan healthPollInterval) : IProvisioningResources
{
    public async Task<ResourceResult> PreflightAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(instancesRoot))!;
            if (new DriveInfo(root).AvailableFreeSpace < minimumFreeDiskBytes)
            {
                return ResourceResult.Failure("insufficient_disk");
            }
        }
        catch (IOException)
        {
            return ResourceResult.Failure("storage_unavailable");
        }

        return await docker.CheckCapacityAsync(cancellationToken);
    }

    public Task<ResourceResult> CreateFilesAsync(
        ProvisioningSpec spec,
        ProvisionBotRequest request,
        CancellationToken cancellationToken = default) =>
        instances.CreateAsync(spec, request, cancellationToken);

    public Task<ResourceResult> CreateContainerAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) => docker.CreateAsync(spec, cancellationToken);

    public Task<ResourceResult> WaitInternalHealthAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) =>
        docker.WaitHealthyAsync(spec, healthTimeout, healthPollInterval, cancellationToken);

    public Task<ResourceResult> AddRouteAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) => caddy.AddAsync(spec, cancellationToken);

    public async Task<ResourceResult> WaitExternalHealthAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(healthTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{spec.PublicHost}/healthz");
            using HttpResponseMessage response = await externalHealthClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.StatusCode == HttpStatusCode.OK
                ? ResourceResult.Success()
                : ResourceResult.Failure("external_health_failed");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ResourceResult.Failure("external_health_timeout");
        }
        catch (HttpRequestException)
        {
            return ResourceResult.Failure("external_health_unavailable");
        }
    }

    public Task<ResourceResult> RemoveRouteAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) => caddy.RemoveAsync(spec, cancellationToken);

    public Task<ResourceResult> RemoveContainerAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) => docker.RemoveAsync(spec, cancellationToken);

    public Task<ResourceResult> RemoveFilesAsync(
        ProvisioningSpec spec,
        CancellationToken cancellationToken = default) => instances.RemoveAsync(spec, cancellationToken);
}
