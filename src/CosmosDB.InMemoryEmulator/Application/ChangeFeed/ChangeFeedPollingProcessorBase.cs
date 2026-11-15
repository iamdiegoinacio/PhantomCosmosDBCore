#nullable enable
using System.Net;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.Application.ChangeFeed;

/// <summary>
/// Template Method base class for all in-memory Change Feed polling processors.
/// Centralizes lifecycle management (<see cref="StartAsync"/> / <see cref="StopAsync"/>),
/// background polling loop, and checkpoint-based iterator draining.
/// </summary>
internal abstract class ChangeFeedPollingProcessorBase : ChangeFeedProcessor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    protected readonly InMemoryContainer Container;
    protected long Checkpoint;

    private CancellationTokenSource? _cts;
    private Task? _pollingTask;

    protected ChangeFeedPollingProcessorBase(InMemoryContainer container)
    {
        Container = container;
    }

    public override Task StartAsync()
    {
        Checkpoint = Container.GetChangeFeedCheckpoint();
        _cts = new CancellationTokenSource();
        _pollingTask = PollLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public override async Task StopAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_pollingTask is not null)
            {
                try
                {
                    await _pollingTask;
                }
                catch (OperationCanceledException)
                {
                }
            }

            _cts.Dispose();
            _cts = null;
            _pollingTask = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var context = new InMemoryChangeFeedProcessorContext();

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, cancellationToken);
            await ProcessIterationAsync(context, cancellationToken);
        }
    }

    protected async Task<List<TItem>> ReadPendingChangesAsync<TItem>(CancellationToken cancellationToken)
    {
        var iterator = Container.GetChangeFeedIterator<TItem>(Checkpoint);
        var changes = new List<TItem>();

        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                break;
            }

            changes.AddRange(response);
        }

        return changes;
    }

    protected abstract Task ProcessIterationAsync(
        InMemoryChangeFeedProcessorContext context,
        CancellationToken cancellationToken);
}
