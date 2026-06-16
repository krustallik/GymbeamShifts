using Xunit;

namespace GymBeamShiftsControllerX.Tests;

[CollectionDefinition("MutableEnvironment")]
public sealed class MutableEnvironmentCollection : ICollectionFixture<MutableEnvironmentFixture>
{
}

public sealed class MutableEnvironmentFixture
{
}
