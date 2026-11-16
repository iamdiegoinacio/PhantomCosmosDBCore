#nullable enable
using Phantom.CosmosDB.Core.Infrastructure.FeedIterators;
using Phantom.CosmosDB.Core.Infrastructure.SdkInterop;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using CosmosResponseFactory = Phantom.CosmosDB.Core.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace Phantom.CosmosDB.Core.Application.ChangeFeed;

/// <summary>
/// Creates typed and stream <see cref="FeedIterator"/> instances for container change feed queries.
/// </summary>
internal static class ContainerChangeFeedIteratorFactory
{
    public static FeedIterator<T> CreateTypedIterator<T>(
        InMemoryChangeFeedLog changeFeedLog,
        ChangeFeedStartFrom changeFeedStartFrom,
        ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions,
        JsonSerializerSettings jsonSettings)
    {
        var pageSize = changeFeedRequestOptions?.PageSizeHint;
        var typeName = changeFeedStartFrom.GetType().Name;
        var feedRange = CosmosSdkIntrospector.ExtractFeedRangeFromStartFrom(changeFeedStartFrom);

        if (typeName.Contains("Now", StringComparison.OrdinalIgnoreCase) ||
            typeName.Contains("Time", StringComparison.OrdinalIgnoreCase))
        {
            DateTimeOffset? capturedTimestamp = null;
            if (typeName.Contains("Now", StringComparison.OrdinalIgnoreCase))
            {
                capturedTimestamp = changeFeedLog.GetNowTimestamp();
            }
            else
            {
                var startTime = CosmosSdkIntrospector.ExtractStartTime(changeFeedStartFrom);
                if (startTime.HasValue)
                    capturedTimestamp = new DateTimeOffset(startTime.Value, TimeSpan.Zero);
            }

            var isNowStart = typeName.Contains("Now", StringComparison.OrdinalIgnoreCase);
            var ts = capturedTimestamp;
            var capturedRange = feedRange;
            return new InMemoryFeedIterator<T>(() =>
            {
                var entries = changeFeedLog.GetFilteredEntries(ts, isNowStart);
                entries = FeedRangeCalculator.FilterChangeFeedEntriesByFeedRange(entries, capturedRange);

                if (changeFeedMode == ChangeFeedMode.Incremental)
                {
                    entries = ProjectIncremental(entries);
                }

                return entries
                    .Select(entry => JsonConvert.DeserializeObject<T>(entry.Json, jsonSettings)!)
                    .ToList();
            }, pageSize);
        }

        var eagerEntries = changeFeedLog.GetSnapshot();
        eagerEntries = FeedRangeCalculator.FilterChangeFeedEntriesByFeedRange(eagerEntries, feedRange);

        if (changeFeedMode == ChangeFeedMode.Incremental)
        {
            eagerEntries = ProjectIncremental(eagerEntries);
        }

        var items = eagerEntries
            .Select(entry => JsonConvert.DeserializeObject<T>(entry.Json, jsonSettings)!)
            .ToList();
        return new InMemoryFeedIterator<T>(items, pageSize);
    }

    public static FeedIterator<T> CreateCheckpointIterator<T>(
        InMemoryChangeFeedLog changeFeedLog,
        long checkpoint,
        JsonSerializerSettings jsonSettings)
    {
        var items = changeFeedLog.GetSnapshot()
            .Skip((int)checkpoint)
            .Select(entry => JsonConvert.DeserializeObject<T>(entry.Json, jsonSettings)!)
            .ToList();
        return new InMemoryFeedIterator<T>(items);
    }

    public static FeedIterator CreateStreamIterator(
        InMemoryChangeFeedLog changeFeedLog,
        ChangeFeedStartFrom changeFeedStartFrom,
        ChangeFeedMode changeFeedMode,
        ChangeFeedRequestOptions? changeFeedRequestOptions,
        Func<string> getSessionToken)
    {
        var feedRange = CosmosSdkIntrospector.ExtractFeedRangeFromStartFrom(changeFeedStartFrom);
        var pageSizeHint = changeFeedRequestOptions?.PageSizeHint;
        var creationTime = DateTimeOffset.UtcNow;
        return CosmosResponseFactory.CreateStreamFeedIteratorFromFactory(() =>
        {
            var entries = changeFeedLog.FilterByStartFrom(changeFeedStartFrom, creationTime);
            entries = FeedRangeCalculator.FilterChangeFeedEntriesByFeedRange(entries, feedRange);

            if (changeFeedMode == ChangeFeedMode.Incremental)
            {
                entries = ProjectIncremental(entries);
            }

            return entries.Select(entry => entry.Json).ToList();
        }, getSessionToken, 0, pageSizeHint);
    }

    private static List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> ProjectIncremental(
        List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> entries)
    {
        return entries
            .GroupBy(entry => (entry.Id, entry.PartitionKey))
            .Select(group => group.Last())
            .Where(entry => !entry.IsDelete)
            .ToList();
    }
}
