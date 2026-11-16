#nullable enable
using System.Net;
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using CosmosDB.InMemoryEmulator.Domain.Validation;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using CosmosDB.InMemoryEmulator.Infrastructure.Storage;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;
using CosmosResponseFactory = CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace CosmosDB.InMemoryEmulator.Application.Account;

/// <summary>
/// Manages container lifecycle operations (Read, Replace, Delete) and throughput configuration.
/// </summary>
internal static class ContainerLifecycleService
{
    public static Task<ContainerResponse> ReadContainerAsync(
        InMemoryContainer container,
        InMemoryItemStore store,
        ContainerProperties properties,
        JsonSerializerSettings jsonSettings,
        CancellationToken cancellationToken)
    {
        _ = jsonSettings;
        cancellationToken.ThrowIfCancellationRequested();
        if (container._isDeleted || (!container.ExplicitlyCreated && store.Items.IsEmpty))
        {
            throw InMemoryCosmosException.Create(
                $"Container '{container.Id}' not found.",
                HttpStatusCode.NotFound,
                CosmosSubStatusCodes.OwnerResourceNotFound,
                Guid.NewGuid().ToString(),
                SyntheticRequestCharge);
        }

        properties.IndexingPolicy = container.IndexingPolicy;
        properties.DefaultTimeToLive = container.DefaultTimeToLive;
        return Task.FromResult(CosmosResponseFactory.CreateContainerResponse(container, properties, HttpStatusCode.OK));
    }

    public static Task<ResponseMessage> ReadContainerStreamAsync(
        InMemoryContainer container,
        InMemoryItemStore store,
        ContainerProperties properties,
        JsonSerializerSettings jsonSettings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (container._isDeleted || (!container.ExplicitlyCreated && store.Items.IsEmpty))
        {
            return Task.FromResult(container.CreateResponseMessage(HttpStatusCode.NotFound));
        }

        properties.IndexingPolicy = container.IndexingPolicy;
        properties.DefaultTimeToLive = container.DefaultTimeToLive;
        return Task.FromResult(container.CreateResponseMessage(
            HttpStatusCode.OK, JsonConvert.SerializeObject(properties, jsonSettings)));
    }

    public static ContainerProperties ApplyReplaceContainer(
        InMemoryContainer container,
        ContainerProperties existingProperties,
        ContainerProperties updatedProperties)
    {
        DocumentValidator.ValidateContainerReplace(existingProperties, updatedProperties);
        DocumentValidator.ValidateComputedProperties(updatedProperties);
        if (existingProperties.UniqueKeyPolicy?.UniqueKeys?.Count > 0 &&
            (updatedProperties.UniqueKeyPolicy is null || updatedProperties.UniqueKeyPolicy.UniqueKeys.Count == 0))
        {
            updatedProperties.UniqueKeyPolicy = existingProperties.UniqueKeyPolicy;
        }

        container.DefaultTimeToLive = updatedProperties.DefaultTimeToLive;
        if (updatedProperties.IndexingPolicy is not null)
        {
            container.IndexingPolicy = updatedProperties.IndexingPolicy;
        }

        container.ResetComputedPropertiesCache();
        return updatedProperties;
    }

    public static Task<ThroughputResponse> ReplaceThroughputAsync(
        InMemoryContainer container,
        int throughput)
    {
        DocumentValidator.ValidateThroughput(throughput, isAutoscale: false);
        container._throughput = throughput;
        return Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(
            ThroughputProperties.CreateManualThroughput(throughput)));
    }

    public static Task<ThroughputResponse> ReplaceThroughputAsync(
        InMemoryContainer container,
        ThroughputProperties throughputProperties)
    {
        if (throughputProperties.AutoscaleMaxThroughput.HasValue)
        {
            DocumentValidator.ValidateThroughput(throughputProperties.AutoscaleMaxThroughput, isAutoscale: true);
            container._throughput = throughputProperties.AutoscaleMaxThroughput.Value;
        }
        else if (throughputProperties.Throughput.HasValue)
        {
            DocumentValidator.ValidateThroughput(throughputProperties.Throughput, isAutoscale: false);
            container._throughput = throughputProperties.Throughput.Value;
        }

        return Task.FromResult(CosmosResponseFactory.CreateThroughputResponse(throughputProperties));
    }
}
