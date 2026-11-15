#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Configuration for a single in-memory Cosmos container.
/// </summary>
public record ContainerConfig(
    string ContainerName,
    string PartitionKeyPath = "/id",
    string? DatabaseName = null,
    ContainerProperties? ContainerProperties = null)
{
    internal static ContainerConfig FromContainerProperties(
        ContainerProperties containerProperties,
        string? databaseName = null)
    {
        string pkPath;
        try
        {
            pkPath = containerProperties.PartitionKeyPath;
        }
        catch (NotImplementedException)
        {
            pkPath = containerProperties.PartitionKeyPaths[0];
        }

        return new ContainerConfig(
            containerProperties.Id,
            pkPath,
            databaseName,
            containerProperties);
    }
}
