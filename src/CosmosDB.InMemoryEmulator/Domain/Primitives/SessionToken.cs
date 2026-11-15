#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Represents a Cosmos DB session consistency token (<c>0:{sequence}#{sequence}</c>).
/// </summary>
internal readonly record struct SessionToken(long Sequence)
{
    public string Value => $"0:{Sequence}#{Sequence}";

    public override string ToString() => Value;
}
