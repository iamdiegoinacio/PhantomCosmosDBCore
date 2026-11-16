#nullable enable
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Strategy for building the query plan response returned to the Cosmos SDK.
/// The SDK sends a query plan request on non-Windows platforms (where ServiceInterop
/// is unavailable) and uses the response to configure its internal pipeline stages.
/// </summary>
public interface IQueryPlanStrategy
{
    /// <summary>
    /// Builds a <c>PartitionedQueryExecutionInfo</c> JSON object for the given SQL query.
    /// </summary>
    JObject BuildQueryPlan(string sqlQuery, CosmosSqlQuery? parsed, string collectionRid);
}
