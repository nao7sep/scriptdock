using Xunit;

namespace ScriptDock.Tests.Storage;

/// <summary>
/// Test collection for the classes that relocate the storage root through the process-wide
/// <c>SCRIPTDOCK_HOME</c> environment variable. Grouping them disables parallel execution across the
/// classes, so one test's temporary <c>SCRIPTDOCK_HOME</c> can never leak into another's resolution.
/// </summary>
[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class StorageRootEnvironment
{
    public const string CollectionName = "StorageRoot environment (SCRIPTDOCK_HOME)";
}
