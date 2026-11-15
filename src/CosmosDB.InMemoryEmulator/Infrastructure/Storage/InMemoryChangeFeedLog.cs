#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Validation;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.Storage;

/// <summary>
/// Thread-safe append-only change feed log with bounded capacity, LSN generation,
/// session token tracking, and delete tombstone formatting.
/// </summary>
internal sealed class InMemoryChangeFeedLog
{
    private readonly List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> _entries = new();
    private readonly object _lock = new();
    private long _lsnCounter;
    private long _sessionSequence;

    public int MaxChangeFeedSize { get; set; } = 1000;

    public string CurrentSessionToken => $"0:{_sessionSequence}#{_sessionSequence}";

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    public void RecordChangeFeed(string id, string? partitionKey, string json, bool isDelete = false)
    {
        lock (_lock)
        {
            _sessionSequence++;
            var lsn = _lsnCounter++;
            var jsonWithLsn = json.Insert(1, $"\"_lsn\":{lsn},");
            _entries.Add((DateTimeOffset.UtcNow, id, partitionKey ?? "", jsonWithLsn, isDelete));

            if (MaxChangeFeedSize > 0 && _entries.Count > MaxChangeFeedSize)
            {
                var excess = _entries.Count - MaxChangeFeedSize;
                _entries.RemoveRange(0, excess);
            }
        }
    }

    public void RecordDeleteTombstone(
        string id,
        string? pk,
        IReadOnlyList<string> partitionKeyPaths,
        PartitionKey partitionKey = default,
        bool isTtlEviction = false)
    {
        var tombstone = new JObject
        {
            ["id"] = id,
            ["_deleted"] = true,
            ["_ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        if (isTtlEviction) tombstone["_ttlEviction"] = true;

        if (partitionKeyPaths.Count > 1 && partitionKey != default && partitionKey != PartitionKey.None && partitionKey != PartitionKey.Null)
        {
            var raw = partitionKey.ToString();
            try
            {
                var arr = JArray.Parse(raw);
                for (var i = 0; i < partitionKeyPaths.Count && i < arr.Count; i++)
                {
                    tombstone[partitionKeyPaths[i].TrimStart('/')] = arr[i].Type == JTokenType.String
                        ? arr[i].Value<string>()
                        : arr[i].ToString();
                }
            }
            catch
            {
                var pkValues = (pk ?? string.Empty).Split('|');
                for (var i = 0; i < partitionKeyPaths.Count; i++)
                {
                    tombstone[partitionKeyPaths[i].TrimStart('/')] = i < pkValues.Length
                        ? PartitionKeyProcessor.TypedKeyToJToken(pkValues[i])
                        : null;
                }
            }
        }
        else if (pk is not null)
        {
            if (partitionKeyPaths.Count == 1)
            {
                tombstone[partitionKeyPaths[0].TrimStart('/')] = PartitionKeyProcessor.TypedKeyToJToken(pk);
            }
            else
            {
                var pkValues = pk.Split('|');
                for (var i = 0; i < partitionKeyPaths.Count; i++)
                {
                    tombstone[partitionKeyPaths[i].TrimStart('/')] = i < pkValues.Length
                        ? PartitionKeyProcessor.TypedKeyToJToken(pkValues[i])
                        : null;
                }
            }
        }

        RecordChangeFeed(id, pk, tombstone.ToString(Formatting.None), isDelete: true);
    }

    public DateTimeOffset GetNowTimestamp()
    {
        lock (_lock)
        {
            return _entries.Count > 0 ? _entries[^1].Timestamp : DateTimeOffset.UtcNow;
        }
    }

    public List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> GetSnapshot()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    public List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> GetSnapshotUpTo(
        DateTimeOffset pointInTime)
    {
        lock (_lock)
        {
            return _entries.Where(e => e.Timestamp <= pointInTime).ToList();
        }
    }

    public List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> GetFilteredEntries(
        DateTimeOffset? timestamp, bool isNowStart)
    {
        lock (_lock)
        {
            return timestamp.HasValue
                ? _entries.Where(entry => isNowStart
                    ? entry.Timestamp > timestamp.Value
                    : entry.Timestamp >= timestamp.Value).ToList()
                : _entries.ToList();
        }
    }

    public List<(DateTimeOffset Timestamp, string Id, string PartitionKey, string Json, bool IsDelete)> FilterByStartFrom(
        ChangeFeedStartFrom startFrom, DateTimeOffset? creationTime = null)
    {
        lock (_lock)
        {
            var typeName = startFrom.GetType().Name;

            if (typeName.Contains("Now", StringComparison.OrdinalIgnoreCase))
            {
                var now = creationTime ?? DateTimeOffset.UtcNow;
                return _entries.Where(entry => entry.Timestamp > now).ToList();
            }

            if (typeName.Contains("Time", StringComparison.OrdinalIgnoreCase))
            {
                var startTime = CosmosSdkIntrospector.ExtractStartTime(startFrom);
                if (startTime.HasValue)
                {
                    var dto = new DateTimeOffset(startTime.Value, TimeSpan.Zero);
                    return _entries.Where(entry => entry.Timestamp >= dto).ToList();
                }
            }

            return _entries.ToList();
        }
    }

    public void TrimToCount(int targetCount)
    {
        lock (_lock)
        {
            while (_entries.Count > targetCount)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
        }
    }
}
