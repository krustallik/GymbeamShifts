namespace GymBeam.AdminManager.Provisioning;

public interface IProvisioningResources
{
    Task<ResourceResult> PreflightAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> CreateFilesAsync(ProvisioningSpec spec, ProvisionBotRequest request, CancellationToken cancellationToken = default);
    Task<ResourceResult> CreateContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> WaitInternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> AddRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> WaitExternalHealthAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> RemoveRouteAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> RemoveContainerAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task<ResourceResult> RemoveFilesAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
}

public interface IProvisioningTransactionStore
{
    Task<bool> BeginAsync(ProvisioningSpec spec, CancellationToken cancellationToken = default);
    Task CompleteAsync(string botId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProvisioningSpec>> GetPendingAsync(CancellationToken cancellationToken = default);
}
