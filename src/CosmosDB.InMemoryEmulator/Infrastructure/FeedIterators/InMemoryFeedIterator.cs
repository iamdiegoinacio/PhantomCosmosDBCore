#nullable enable
using CosmosDB.InMemoryEmulator.Infrastructure.FeedIterators;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// In-memory implementation of <see cref="FeedIterator{T}"/> that pages through a
/// pre-computed list of items. Supports pagination via <c>maxItemCount</c>,
/// continuation tokens (offset-based), and deferred evaluation via factory delegates.
/// </summary>
/// <typeparam name="T">The item type returned by each page.</typeparam>
public class InMemoryFeedIterator<T> : FeedIterator<T>
{
    private IReadOnlyList<T>? _items;
    private readonly Func<IReadOnlyList<T>>? _factory;
    private readonly int? _maxItemCount;
    private int _offset;
    private bool _hasReadFirstPage;

    /// <summary>
    /// Creates a feed iterator from a pre-computed list of items.
    /// </summary>
    public InMemoryFeedIterator(IReadOnlyList<T> items, int? maxItemCount = null, int initialOffset = 0)
    {
        _items = items;
        _maxItemCount = maxItemCount;
        _offset = initialOffset;
    }

    /// <summary>
    /// Creates a feed iterator from an enumerable source, materialising it eagerly.
    /// </summary>
    public InMemoryFeedIterator(IEnumerable<T> source, int? maxItemCount = null)
    {
        _items = Materialize(source);
        _maxItemCount = maxItemCount;
    }

    /// <summary>
    /// Creates a feed iterator with deferred evaluation. The factory is called
    /// on the first access to produce the list of items.
    /// </summary>
    public InMemoryFeedIterator(Func<IReadOnlyList<T>> factory, int? maxItemCount = null)
    {
        _factory = factory;
        _maxItemCount = maxItemCount;
    }

    private static IReadOnlyList<T> Materialize(IEnumerable<T> source)
    {
        try
        {
            return (source as IReadOnlyList<T>) ?? source.ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to materialize InMemoryFeedIterator<{typeof(T).Name}> source enumerable. " +
                "This may indicate an error in the LINQ query or the underlying data source.", ex);
        }
    }

    private int PageSize => _maxItemCount is > 0 ? _maxItemCount.Value : EnsureItems().Count;

    /// <summary>
    /// When true, <see cref="FeedResponse{T}.IndexMetrics"/> returns a synthetic stub
    /// instead of null, matching real Cosmos DB behaviour when PopulateIndexMetrics is requested.
    /// </summary>
    public bool PopulateIndexMetrics { get; set; }

    /// <summary>
    /// When true, <see cref="HasMoreResults"/> returns true before the first page is read,
    /// even when the result set is empty. This matches real Cosmos DB query iterator behavior.
    /// </summary>
    public bool GuaranteeFirstPage { get; set; }

    public override bool HasMoreResults => (GuaranteeFirstPage && !_hasReadFirstPage) || _offset < EnsureItems().Count;

    public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _hasReadFirstPage = true;
        var items = EnsureItems();
        var page = items.Skip(_offset).Take(PageSize).ToList();
        _offset += page.Count;
        if (_offset >= items.Count)
        {
            _offset = items.Count;
        }

        var continuationToken = _offset < items.Count ? _offset.ToString() : null;
        return Task.FromResult<FeedResponse<T>>(
            new InMemoryFeedResponse<T>(page, continuationToken, PopulateIndexMetrics));
    }

    private IReadOnlyList<T> EnsureItems()
    {
        if (_items is null && _factory is not null)
        {
            _items = _factory();
        }
        return _items ?? Array.Empty<T>();
    }
}
