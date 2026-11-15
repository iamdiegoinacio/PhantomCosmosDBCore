#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Sentinel singleton representing an <c>undefined</c> value (missing JSON property or
/// invalid expression result) in Cosmos DB SQL three-value logic, distinct from <c>null</c>.
/// </summary>
internal sealed class UndefinedValue
{
    public static readonly UndefinedValue Instance = new();

    private UndefinedValue()
    {
    }

    public override string? ToString() => null;
}
