#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Validation;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;

/// <summary>
/// Generates synthetic <see cref="FeedRange"/> instances and filters items or change feed entries
/// by partition key or MurmurHash3 feed range boundaries.
/// </summary>
internal static class FeedRangeCalculator
{
    public static IReadOnlyList<FeedRange> GetFeedRanges(int feedRangeCount)
    {
        var count = Math.Max(1, feedRangeCount);
        var ranges = new List<FeedRange>(count);
        var step = 0x1_0000_0000L / count;

        for (var i = 0; i < count; i++)
        {
            var min = PartitionKeyHash.RangeBoundaryToHex(i * step);
            var max = (i == count - 1) ? "FF" : PartitionKeyHash.RangeBoundaryToHex((i + 1) * step);
            ranges.Add(FeedRange.FromJsonString($"{{\"Range\":{{\"min\":\"{min}\",\"max\":\"{max}\"}}}}"));
        }

        return ranges;
    }

    public static List<string> FilterByFeedRange(
        List<string> items,
        FeedRange? feedRange,
        IReadOnlyList<string> partitionKeyPaths,
        JsonSerializerSettings jsonSettings)
    {
        var pkValue = TryExtractPartitionKeyFromFeedRange(feedRange);
        if (pkValue != null)
        {
            return items.Where(json =>
            {
                var itemPk = PartitionKeyProcessor.ExtractPartitionKeyValueFromJson(json, partitionKeyPaths, jsonSettings);
                return string.Equals(itemPk, pkValue, StringComparison.Ordinal);
            }).ToList();
        }

        var (min, max) = ParseFeedRangeBoundaries(feedRange);
        if (min == null || max == null) return items;

        return items.Where(json =>
        {
            var itemPk = PartitionKeyProcessor.ExtractPartitionKeyValueFromJson(json, partitionKeyPaths, jsonSettings);
            var hash = PartitionKeyHash.MurmurHash3(itemPk);
            return IsHashInRange(hash, min.Value, max.Value);
        }).ToList();
    }

    public static List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)>
        FilterChangeFeedEntriesByFeedRange(
            List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> entries,
            FeedRange? feedRange)
    {
        var pkValue = TryExtractPartitionKeyFromFeedRange(feedRange);
        if (pkValue != null)
        {
            return entries.Where(entry =>
                string.Equals(entry.PartitionKey ?? "", pkValue, StringComparison.Ordinal)).ToList();
        }

        var (min, max) = ParseFeedRangeBoundaries(feedRange);
        if (min == null || max == null) return entries;

        return entries.Where(entry =>
        {
            var hash = PartitionKeyHash.MurmurHash3(entry.PartitionKey ?? "");
            return IsHashInRange(hash, min.Value, max.Value);
        }).ToList();
    }

    public static (uint? Min, uint? Max) ParseFeedRangeBoundaries(FeedRange? feedRange)
    {
        if (feedRange == null) return (null, null);

        try
        {
            var json = feedRange.ToJsonString();
            var obj = JObject.Parse(json);
            var rangeObj = obj["Range"];
            if (rangeObj == null) return (null, null);

            var minStr = rangeObj["min"]?.ToString() ?? "";
            var maxStr = rangeObj["max"]?.ToString() ?? "";

            var minVal = string.IsNullOrEmpty(minStr) ? 0u : Convert.ToUInt32(minStr, 16);
            var maxVal = string.Equals(maxStr, "FF", StringComparison.OrdinalIgnoreCase)
                ? uint.MaxValue
                : Convert.ToUInt32(maxStr, 16);

            return (minVal, maxVal);
        }
        catch
        {
            return (null, null);
        }
    }

    public static string? TryExtractPartitionKeyFromFeedRange(FeedRange? feedRange)
    {
        if (feedRange == null) return null;
        try
        {
            var json = feedRange.ToJsonString();
            var obj = JObject.Parse(json);
            var pkToken = obj["PK"];
            if (pkToken == null) return null;

            // PK value is a string containing a JSON array, e.g. "[\"pk-5\"]" or "[42]"
            var pkStr = pkToken.ToString();
            var pkArray = JArray.Parse(pkStr);
            if (pkArray.Count == 0) return null;
            // For multi-component (hierarchical) partition keys, join all parts with "|"
            // to match the format used by ExtractPartitionKeyValueCore.
            if (pkArray.Count == 1)
                return PartitionKeyProcessor.JTokenToTypedKey(pkArray[0]) ?? "";
            var parts = pkArray.Select(t => PartitionKeyProcessor.JTokenToTypedKey(t) ?? "").ToList();
            return string.Join("|", parts);
        }
        catch
        {
            return null;
        }
    }

    public static bool IsHashInRange(uint hash, uint min, uint max)
        => hash >= min && (max == uint.MaxValue || hash < max);
}
