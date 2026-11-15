#nullable enable
namespace CosmosDB.InMemoryEmulator.Application.Configuration;

internal sealed record ContainerSpec(
    string Name,
    string[] PartitionKeyPaths,
    Action<IContainerTestSetup>? Configure);

internal sealed record DatabaseSpec(
    string Name,
    List<ContainerSpec> Containers);

internal static class CosmosSetupValidator
{
    public static void ValidateContainerName(string containerName)
    {
        if (string.IsNullOrWhiteSpace(containerName))
            throw new ArgumentException("Container name must not be null, empty, or whitespace.", nameof(containerName));
    }

    public static void ValidatePartitionKeyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Partition key path must not be null or empty.", nameof(path));
        if (!path.StartsWith('/'))
            throw new ArgumentException($"Partition key path must start with '/'. Got: '{path}'.", nameof(path));
    }

    public static void ValidatePartitionKeyPaths(string[] partitionKeyPaths)
    {
        if (partitionKeyPaths is null || partitionKeyPaths.Length == 0)
            throw new ArgumentException("At least one partition key path must be provided.", nameof(partitionKeyPaths));
        foreach (var path in partitionKeyPaths)
            ValidatePartitionKeyPath(path);
    }
}
