#nullable enable
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.ChangeFeed;

internal sealed class InMemoryChangeFeedProcessor<T> : ChangeFeedPollingProcessorBase
{
    private readonly Func<ChangeFeedProcessorContext, IReadOnlyCollection<T>, CancellationToken, Task> _handler;

    internal InMemoryChangeFeedProcessor(
        InMemoryContainer container,
        Container.ChangeFeedHandler<T> handler)
        : base(container)
    {
        _handler = (ctx, changes, ct) => handler(ctx, changes, ct);
    }

    internal InMemoryChangeFeedProcessor(
        InMemoryContainer container,
        Container.ChangesHandler<T> legacyHandler)
        : base(container)
    {
        _handler = (_, changes, ct) => legacyHandler(changes, ct);
    }

    protected override async Task ProcessIterationAsync(
        InMemoryChangeFeedProcessorContext context,
        CancellationToken cancellationToken)
    {
        var changes = await ReadPendingChangesAsync<T>(cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        // Deduplicate: keep only the latest version per item (by id),
        // matching LatestVersion change feed processor behavior.
        var deduped = DeduplicateByItemId(changes);
        try
        {
            await _handler(context, deduped, cancellationToken);
            Checkpoint += changes.Count; // advance past ALL entries, including intermediates
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Handler threw — do not advance checkpoint so the same batch is redelivered
        }
    }

    private static IReadOnlyCollection<T> DeduplicateByItemId(List<T> changes)
    {
        var seen = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in changes)
        {
            var id = ExtractId(item);
            if (id is not null)
            {
                seen[id] = item; // last write wins — keeps latest version per item
            }
        }

        return seen.Count == changes.Count ? changes : seen.Values.ToList();
    }

    private static string? ExtractId(T item)
    {
        if (item is JObject jo)
        {
            return jo["id"]?.ToString();
        }

        var prop = typeof(T).GetProperty("Id") ?? typeof(T).GetProperty("id");
        return prop?.GetValue(item)?.ToString();
    }
}

internal sealed class InMemoryManualCheckpointChangeFeedProcessor<T> : ChangeFeedPollingProcessorBase
{
    private readonly Container.ChangeFeedHandlerWithManualCheckpoint<T> _handler;

    internal InMemoryManualCheckpointChangeFeedProcessor(
        InMemoryContainer container,
        Container.ChangeFeedHandlerWithManualCheckpoint<T> handler)
        : base(container)
    {
        _handler = handler;
    }

    protected override async Task ProcessIterationAsync(
        InMemoryChangeFeedProcessorContext context,
        CancellationToken cancellationToken)
    {
        var changes = await ReadPendingChangesAsync<T>(cancellationToken);
        if (changes.Count == 0)
        {
            return;
        }

        var pendingCheckpoint = Checkpoint + changes.Count;
        var checkpointed = false;
        try
        {
            await _handler(
                context,
                changes,
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
