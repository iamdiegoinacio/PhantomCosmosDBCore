#nullable enable

namespace CosmosDB.InMemoryEmulator.Domain.Primitives;

/// <summary>
/// Official Azure Cosmos DB sub-status codes returned in the <c>x-ms-substatus</c> header
/// and exposed via <see cref="Microsoft.Azure.Cosmos.CosmosException.SubStatusCode"/>.
/// </summary>
public static class CosmosSubStatusCodes
{
    public const int Unknown = 0;

    /// <summary>
    /// Returned when a custom JavaScript stored procedure or trigger throws an unhandled error.
    /// </summary>
    public const int ScriptError = 400;

    /// <summary>
    /// Returned on HTTP 400 when the partition key specified in the request header/parameter
    /// does not match the partition key extracted from the document body.
    /// </summary>
    public const int PartitionKeyMismatch = 1001;

    /// <summary>
    /// Returned on HTTP 404 when the requested session token is not available on the target replica.
    /// </summary>
    public const int SessionNotAvailable = 1002;

    /// <summary>
    /// Returned on HTTP 404 when the parent resource (e.g. database or container) does not exist.
    /// </summary>
    public const int OwnerResourceNotFound = 1003;

    /// <summary>
    /// Returned on HTTP 400 when cross-partition query is not enabled or an index configuration prevents execution.
    /// </summary>
    public const int CrossPartitionQueryNotServable = 1004;

    /// <summary>
    /// Returned on HTTP 400 when a required configuration or resource property is missing.
    /// </summary>
    public const int ConfigurationPropertyNotFound = 1005;

    /// <summary>
    /// Returned on HTTP 409 when a write operation violates a Unique Key constraint on the container.
    /// </summary>
    public const int UniqueKeyConstraintViolation = 1008;

    /// <summary>
    /// Returned on HTTP 404 when a Change Feed lease document is not found.
    /// </summary>
    public const int LeaseNotFound = 1012;

    /// <summary>
    /// Returned on HTTP 429 when Request Units (RU/s) capacity is exceeded (rate-limiting / throttling).
    /// </summary>
    public const int RuBudgetExceeded = 3200;

    /// <summary>
    /// Returned on HTTP 408 when a JavaScript stored procedure execution exceeds the service timeout limit.
    /// </summary>
    public const int StoredProcedureScriptTimeout = 3207;
}
