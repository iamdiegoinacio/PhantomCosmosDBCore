#nullable enable
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator;

/// <summary>
/// Engine for executing JavaScript stored procedure bodies.
/// Implemented by the optional <c>CosmosDB.InMemoryEmulator.JsTriggers</c> package.
/// </summary>
public interface ISprocEngine
{
    /// <summary>
    /// Executes a stored procedure JavaScript body and returns the result set via <c>response.setBody()</c>.
    /// </summary>
    string? Execute(string jsBody, PartitionKey partitionKey, dynamic[] args);

    /// <summary>
    /// Executes a stored procedure JavaScript body with access to the collection context for CRUD operations.
    /// </summary>
    string? Execute(string jsBody, PartitionKey partitionKey, dynamic[] args, ICollectionContext context);

    /// <summary>
    /// Log messages captured from <c>console.log()</c> calls during the most recent <see cref="Execute"/> invocation.
    /// </summary>
    IReadOnlyList<string> CapturedLogs { get; }
}
