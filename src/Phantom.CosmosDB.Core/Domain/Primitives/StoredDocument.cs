#nullable enable

namespace Phantom.CosmosDB.Core.Domain.Primitives;

/// <summary>
/// Represents a document stored in the in-memory container along with its system metadata.
/// </summary>
internal sealed record StoredDocument(
    ItemKey Key,
    string RawJson,
    string ETag,
    DateTimeOffset Timestamp);
