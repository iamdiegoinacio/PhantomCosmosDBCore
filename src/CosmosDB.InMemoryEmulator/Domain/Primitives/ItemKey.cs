#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Strongly-typed composite key identifying a document by its logical <see cref="Id"/>
/// and serialized <see cref="PartitionKey"/> within a container.
/// </summary>
internal readonly record struct ItemKey(string Id, string? PartitionKey)
{
    public static ItemKey Create(string id, string? partitionKey) => new(id, partitionKey);

    public static implicit operator (string Id, string? PartitionKey)(ItemKey key) => (key.Id, key.PartitionKey);

    public static implicit operator ItemKey((string Id, string? PartitionKey) tuple) => new(tuple.Id, tuple.PartitionKey);
}
