#nullable enable
using CosmosDB.InMemoryEmulator.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Crud;

/// <summary>Executes the pre-triggers requested by <paramref name="requestOptions"/> and returns the (possibly mutated) document.</summary>
internal delegate JObject PreTriggerExecutor(ItemRequestOptions? requestOptions, JObject document, string operationName);

/// <summary>Executes the post-triggers requested by <paramref name="requestOptions"/>; returns a response body override, if any.</summary>
internal delegate JObject? PostTriggerExecutor(ItemRequestOptions? requestOptions, JObject committedDocument, string operationName);

/// <summary>
/// Parameter Object bundling everything a single-item operation needs from its owning container.
/// Created per operation so that container-level changes (TTL, properties) are always observed.
/// </summary>
internal sealed record DocumentOperationContext(
    InMemoryItemStore Store,
    InMemoryChangeFeedLog ChangeFeedLog,
    string ContainerId,
    IReadOnlyList<string> PartitionKeyPaths,
    ContainerProperties ContainerProperties,
    int? DefaultTimeToLive,
    JsonSerializerSettings JsonSettings,
    PreTriggerExecutor ExecutePreTriggers,
    PostTriggerExecutor ExecutePostTriggers)
{
    /// <summary>Session token reflecting the latest successful write.</summary>
    public string SessionToken => ChangeFeedLog.CurrentSessionToken;

    /// <summary>Whether the container defines unique keys, which requires serialised writes.</summary>
    public bool HasUniqueKeys => ContainerProperties.UniqueKeyPolicy?.UniqueKeys.Count > 0;
}
