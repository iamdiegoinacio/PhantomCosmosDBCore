#nullable enable
using System.Net;
using Microsoft.Azure.Cosmos;

namespace Phantom.CosmosDB.Core;

/// <summary>
/// Factory for creating <see cref="CosmosException"/> instances that match the exact
/// <see cref="CosmosException"/> type (not a subclass).
/// </summary>
public static class InMemoryCosmosException
{
    /// <summary>
    /// Creates a new <see cref="CosmosException"/>.
    /// </summary>
    public static CosmosException Create(
        string message,
        HttpStatusCode statusCode,
        int subStatusCode,
        string activityId,
        double requestCharge)
    {
        return new CosmosException(message, statusCode, subStatusCode, activityId, requestCharge);
    }
}
