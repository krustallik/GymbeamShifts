namespace GymBeam.AdminManager.Registry;

public interface IManagedBotRegistry
{
    Task<IReadOnlyList<ManagedBot>> GetAllAsync(CancellationToken cancellationToken = default);
}

public interface IManagedBotRegistryMutations : IManagedBotRegistry
{
    Task AddActiveAsync(ManagedBot bot, CancellationToken cancellationToken = default);
    Task UpdateAsync(ManagedBot bot, CancellationToken cancellationToken = default);
    Task RemoveAsync(string botId, CancellationToken cancellationToken = default);
}
