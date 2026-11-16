#nullable enable
using Phantom.CosmosDB.Core.Domain.Primitives;
using Phantom.CosmosDB.Core.Domain.Validation;
using Phantom.CosmosDB.Core.Infrastructure.Storage;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>The ETag and enriched (system-property decorated) JSON of a committed write.</summary>
internal readonly record struct CommittedWrite(string ETag, string Json);

/// <summary>Point-in-time copy of an item's stored state, used to roll a failed operation back.</summary>
internal readonly record struct EntrySnapshot(string? Json, string? ETag, DateTimeOffset? Timestamp)
{
    public bool Exists => Json is not null;
}

/// <summary>
/// Unit-of-work primitives shared by every single-item write: per-item locking, committing a
/// document with fresh system properties, snapshotting and rolling back on failure.
/// </summary>
internal static class DocumentWriteCommitter
{
    /// <summary>Runs <paramref name="body"/> while holding the per-item lock.</summary>
    public static async Task<TResult> WithItemLockAsync<TResult>(
        InMemoryItemStore store,
        (string Id, string PartitionKey) key,
        Func<TResult> body,
        CancellationToken cancellationToken)
    {
        var itemLock = store.GetOrAddLock(key);
        await itemLock.WaitAsync(cancellationToken);
        try
        {
            return body();
        }
        finally
        {
            itemLock.Release();
        }
    }

    /// <summary>Captures the currently stored state of <paramref name="key"/> (empty snapshot if absent).</summary>
    public static EntrySnapshot Capture(InMemoryItemStore store, (string Id, string PartitionKey) key)
        => store.Items.TryGetValue(key, out var json) ? Capture(store, key, json) : default;

    /// <summary>Captures ETag/timestamp of <paramref name="key"/> paired with an already-read <paramref name="json"/>.</summary>
    public static EntrySnapshot Capture(InMemoryItemStore store, (string Id, string PartitionKey) key, string json)
        => new(
            json,
            store.Etags.TryGetValue(key, out var etag) ? etag : null,
            store.Timestamps.TryGetValue(key, out var timestamp) ? timestamp : null);

    /// <summary>Stores <paramref name="json"/> under a new ETag/timestamp, enriched with system properties.</summary>
    public static CommittedWrite Write(
        DocumentOperationContext context,
        (string Id, string PartitionKey) key,
        string json)
    {
        var store = context.Store;
        var etag = ETagValue.GenerateNextString();
        var timestamp = DateTimeOffset.UtcNow;
        store.Etags[key] = etag;
        store.Timestamps[key] = timestamp;
        var enriched = InMemoryItemStore.EnrichWithSystemProperties(context.ContainerId, json, etag, timestamp);
        store.Items[key] = enriched;
        return new CommittedWrite(etag, enriched);
    }

    /// <summary>
    /// Re-serialises a post-trigger-mutated document and, when it changed, persists it after
    /// validating its size.
    /// </summary>
    public static void ApplyPostTriggerChanges(
        InMemoryItemStore store,
        (string Id, string PartitionKey) key,
        JObject committedDocument)
    {
        var postTriggerJson = committedDocument.ToString(Formatting.None);
        if (postTriggerJson != store.Items[key])
        {
            DocumentValidator.ValidateDocumentSize(postTriggerJson);
            store.Items[key] = postTriggerJson;
        }
    }

    /// <summary>Removes every trace of <paramref name="key"/> (item, ETag, timestamp, insertion order).</summary>
    public static void Remove(InMemoryItemStore store, (string Id, string PartitionKey) key)
    {
        store.Items.TryRemove(key, out _);
        store.Etags.TryRemove(key, out _);
        store.Timestamps.TryRemove(key, out _);
        store.RemoveInsertionOrder(key);
    }

    /// <summary>Restores a previously captured state (insertion order is left untouched).</summary>
    public static void Restore(InMemoryItemStore store, (string Id, string PartitionKey) key, EntrySnapshot snapshot)
    {
        store.Items[key] = snapshot.Json!;
        if (snapshot.ETag is not null) store.Etags[key] = snapshot.ETag;
        if (snapshot.Timestamp.HasValue) store.Timestamps[key] = snapshot.Timestamp.Value;
    }

    /// <summary>Restores an item that a failed delete had removed, including its insertion order.</summary>
    public static void RestoreDeleted(InMemoryItemStore store, (string Id, string PartitionKey) key, EntrySnapshot snapshot)
    {
        Restore(store, key, snapshot);
        store.AddInsertionOrder(key);
    }

    /// <summary>Undoes a failed upsert: restores the previous version, or removes the item if it was new.</summary>
    public static void RollbackUpsert(InMemoryItemStore store, (string Id, string PartitionKey) key, EntrySnapshot previous)
    {
        if (previous.Exists)
        {
            Restore(store, key, previous);
        }
        else
        {
            Remove(store, key);
        }
    }
}
