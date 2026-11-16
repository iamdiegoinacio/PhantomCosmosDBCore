#nullable enable
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Wraps a <see cref="FeedIterator{T}"/> to set the partition key override
/// on the owning <see cref="FakeCosmosHandler"/> around each <see cref="ReadNextAsync"/> call.
/// </summary>
internal sealed class PkCapturingFeedIterator<T> : FeedIterator<T>
{
    private readonly FeedIterator<T> _inner;
    private readonly FakeCosmosHandler _handler;
    private readonly PartitionKey _pk;

    internal PkCapturingFeedIterator(FeedIterator<T> inner, FakeCosmosHandler handler, PartitionKey pk)
    {
        _inner = inner;
        _handler = handler;
        _pk = pk;
    }

    public override bool HasMoreResults => _inner.HasMoreResults;

    public override async Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        using (_handler.WithPartitionKey(_pk))
        {
            return await _inner.ReadNextAsync(cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Wraps a <see cref="FeedIterator"/> (stream) to set the partition key override
/// on the owning <see cref="FakeCosmosHandler"/> around each <see cref="ReadNextAsync"/> call.
/// </summary>
internal sealed class PkCapturingStreamFeedIterator : FeedIterator
{
    private readonly FeedIterator _inner;
    private readonly FakeCosmosHandler _handler;
    private readonly PartitionKey _pk;

    internal PkCapturingStreamFeedIterator(FeedIterator inner, FakeCosmosHandler handler, PartitionKey pk)
    {
        _inner = inner;
        _handler = handler;
        _pk = pk;
    }

    public override bool HasMoreResults => _inner.HasMoreResults;

    public override async Task<ResponseMessage> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        using (_handler.WithPartitionKey(_pk))
        {
            return await _inner.ReadNextAsync(cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
