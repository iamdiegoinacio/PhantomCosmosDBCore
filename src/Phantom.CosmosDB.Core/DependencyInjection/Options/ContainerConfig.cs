#nullable enable
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Configuration for a single in-memory Cosmos container.
/// </summary>
public record ContainerConfig(
    string ContainerName,
    string PartitionKeyPath = "/id",
    string? DatabaseName = null,
    ContainerProperties? ContainerProperties = null,
    int? Throughput = null)
{
    internal static ContainerConfig FromContainerProperties(
        ContainerProperties containerProperties,
        string? databaseName = null,
        int? throughput = null)
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
            containerProperties,
            throughput);
    }
}
