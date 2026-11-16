#nullable enable
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core.Application.ChangeFeed;

internal sealed class InMemoryChangeFeedProcessorContext : ChangeFeedProcessorContext
{
    public override string LeaseToken { get; } = "0";
    public override CosmosDiagnostics Diagnostics => null!;
    public override Headers Headers { get; } = new();
    public override FeedRange FeedRange => FeedRange.FromPartitionKey(PartitionKey.None);
}

internal sealed class NoOpChangeFeedProcessor : ChangeFeedProcessor
{
    public override Task StartAsync() => Task.CompletedTask;
    public override Task StopAsync() => Task.CompletedTask;
}
