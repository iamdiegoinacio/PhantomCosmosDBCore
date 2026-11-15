#nullable enable
using System.Text;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.ChangeFeed;

internal sealed class InMemoryChangeFeedStreamProcessor : ChangeFeedPollingProcessorBase
{
    private readonly Container.ChangeFeedStreamHandler _handler;

    internal InMemoryChangeFeedStreamProcessor(
        InMemoryContainer container,
        Container.ChangeFeedStreamHandler handler)
        : base(container)
    {
        _handler = handler;
    }

    protected override async Task ProcessIterationAsync(
        InMemoryChangeFeedProcessorContext context,
        CancellationToken cancellationToken)
    {
        var changes = await ReadPendingChangesAsync<JObject>(cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        try
        {
            var array = new JArray(changes);
            var bytes = Encoding.UTF8.GetBytes(array.ToString());
            using var stream = new MemoryStream(bytes);
            await _handler(context, stream, cancellationToken);
            Checkpoint += changes.Count;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Handler threw — do not advance checkpoint so the same batch is redelivered
        }
    }
}

internal sealed class InMemoryManualCheckpointStreamChangeFeedProcessor : ChangeFeedPollingProcessorBase
{
    private readonly Container.ChangeFeedStreamHandlerWithManualCheckpoint _handler;

    internal InMemoryManualCheckpointStreamChangeFeedProcessor(
        InMemoryContainer container,
        Container.ChangeFeedStreamHandlerWithManualCheckpoint handler)
        : base(container)
    {
        _handler = handler;
    }

    protected override async Task ProcessIterationAsync(
        InMemoryChangeFeedProcessorContext context,
        CancellationToken cancellationToken)
    {
        var changes = await ReadPendingChangesAsync<JObject>(cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        var pendingCheckpoint = Checkpoint + changes.Count;
        var checkpointed = false;
        try
        {
            var array = new JArray(changes);
            var bytes = Encoding.UTF8.GetBytes(array.ToString());
            using var stream = new MemoryStream(bytes);
            await _handler(
                context,
                stream,
                async () =>
                {
                    checkpointed = true;
                    await Task.CompletedTask;
                },
                cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Handler threw — do not advance checkpoint
        }

        if (checkpointed)
        {
            Checkpoint = pendingCheckpoint;
        }
    }
}
