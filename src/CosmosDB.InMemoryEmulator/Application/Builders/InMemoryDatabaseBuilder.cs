#nullable enable
using CosmosDB.InMemoryEmulator.Application.Configuration;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Fluent builder for configuring containers within a named database.
/// </summary>
public sealed class InMemoryDatabaseBuilder
{
    internal List<ContainerSpec> Containers { get; } = new();

    /// <summary>
    /// Adds a container with a single partition key path.
    /// </summary>
    public InMemoryDatabaseBuilder AddContainer(
        string name,
        string partitionKeyPath,
        Action<IContainerTestSetup>? configure = null)
    {
        CosmosSetupValidator.ValidateContainerName(name);
        CosmosSetupValidator.ValidatePartitionKeyPath(partitionKeyPath);
        ValidateNoDuplicate(name);
        Containers.Add(new ContainerSpec(name, [partitionKeyPath], configure));
        return this;
    }

    /// <summary>
    /// Adds a container with hierarchical (composite) partition key paths.
    /// </summary>
    public InMemoryDatabaseBuilder AddContainer(
        string name,
        string[] partitionKeyPaths,
        Action<IContainerTestSetup>? configure = null)
    {
        CosmosSetupValidator.ValidateContainerName(name);
        CosmosSetupValidator.ValidatePartitionKeyPaths(partitionKeyPaths);
        ValidateNoDuplicate(name);
        Containers.Add(new ContainerSpec(name, partitionKeyPaths, configure));
        return this;
    }

    private void ValidateNoDuplicate(string name)
    {
        if (Containers.Any(c => c.Name == name))
            throw new InvalidOperationException($"Container '{name}' has already been added.");
    }
}
