#nullable enable
using System.Net;
using Microsoft.Azure.Cosmos;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;

namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>Factory for the Cosmos exceptions raised by typed item operations.</summary>
internal static class DocumentExceptions
{
    public static CosmosException NotFound(string id)
        => Create(
            $"Resource Not Found. Entity with the specified id does not exist in the system. id = {id}",
            HttpStatusCode.NotFound);

    public static CosmosException AlreadyExists(string id)
        => Create(
            $"Entity with the specified id already exists in the system. id = {id}",
            HttpStatusCode.Conflict);

    public static CosmosException BadRequest(string message)
        => Create(message, HttpStatusCode.BadRequest);

    private static CosmosException Create(string message, HttpStatusCode statusCode)
        => InMemoryCosmosException.Create(
            message, statusCode, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
}
