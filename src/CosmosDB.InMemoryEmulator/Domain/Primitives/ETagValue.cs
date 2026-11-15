#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Represents an entity tag (ETag) for optimistic concurrency control in the in-memory emulator.
/// </summary>
internal readonly record struct ETagValue(string Value)
{
    private static long _counter;

    public static ETagValue GenerateNext() => new($"\"{Interlocked.Increment(ref _counter):x16}\"");

    public static string GenerateNextString() => GenerateNext().Value;

    public override string ToString() => Value;

    public static implicit operator string(ETagValue etag) => etag.Value;
}
