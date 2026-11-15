#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.Infrastructure.FeedIterators;

/// <summary>
/// Synthetic <see cref="CosmosDiagnostics"/> shared across in-memory feed and batch responses.
/// </summary>
internal sealed class InMemoryFeedDiagnostics : CosmosDiagnostics
{
    public static readonly CosmosDiagnostics Instance = new InMemoryFeedDiagnostics();

    public override TimeSpan GetClientElapsedTime() => TimeSpan.Zero;
    public override IReadOnlyList<(string regionName, Uri uri)> GetContactedRegions() => Array.Empty<(string, Uri)>();
    public override string ToString() => "{}";
}
