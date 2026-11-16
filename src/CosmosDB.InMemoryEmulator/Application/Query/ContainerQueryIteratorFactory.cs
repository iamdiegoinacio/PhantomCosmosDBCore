#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Validation;
using CosmosDB.InMemoryEmulator.Infrastructure.FeedIterators;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using CosmosResponseFactory = CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop.CosmosResponseFactory;

namespace CosmosDB.InMemoryEmulator.Application.Query;

/// <summary>
/// Creates typed and stream <see cref="FeedIterator"/> instances for container item queries.
/// </summary>
internal static class ContainerQueryIteratorFactory
{
    public static FeedIterator<T> CreateTypedIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        JsonSerializerSettings jsonSettings)
    {
        DocumentValidator.ValidateMaxItemCount(requestOptions);
        var parameters = CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition);
        var filtered = CosmosQueryPipeline.ExecuteSafe(() => filterByQuery(queryDefinition.QueryText, parameters, requestOptions, null));
        var items = filtered.Select(json => JsonConvert.DeserializeObject<T>(json, jsonSettings)!).ToList();
        var initialOffset = DocumentValidator.ParseContinuationToken(continuationToken);
        return new InMemoryFeedIterator<T>(items, requestOptions?.MaxItemCount, initialOffset)
        {
            PopulateIndexMetrics = requestOptions?.PopulateIndexMetrics ?? false,
            GuaranteeFirstPage = true
        };
    }

    public static FeedIterator<T> CreateTypedIterator<T>(
        string? queryText,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        Func<QueryRequestOptions?, IEnumerable<string>> getAllItemsForPartition,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        JsonSerializerSettings jsonSettings)
    {
        DocumentValidator.ValidateMaxItemCount(requestOptions);
        List<T> items;
        if (string.IsNullOrEmpty(queryText))
        {
            items = getAllItemsForPartition(requestOptions)
                .Select(json => JsonConvert.DeserializeObject<T>(json, jsonSettings)!).ToList();
        }
        else
        {
            var filtered = CosmosQueryPipeline.ExecuteSafe(
                () => filterByQuery(queryText, new Dictionary<string, object?>(), requestOptions, null));
            items = filtered.Select(json => JsonConvert.DeserializeObject<T>(json, jsonSettings)!).ToList();
        }

        var initialOffset = DocumentValidator.ParseContinuationToken(continuationToken);
        return new InMemoryFeedIterator<T>(items, requestOptions?.MaxItemCount, initialOffset)
        {
            PopulateIndexMetrics = requestOptions?.PopulateIndexMetrics ?? false,
            GuaranteeFirstPage = true
        };
    }

    public static FeedIterator<T> CreateTypedFeedRangeIterator<T>(
        FeedRange feedRange,
        QueryDefinition queryDefinition,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        IReadOnlyList<string> partitionKeyPaths,
        Func<QueryRequestOptions?, IEnumerable<string>> getAllItemsForPartition,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        JsonSerializerSettings jsonSettings)
    {
        DocumentValidator.ValidateMaxItemCount(requestOptions);
        var parameters = CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition);
        var preFiltered = FeedRangeCalculator.FilterByFeedRange(
            getAllItemsForPartition(requestOptions).ToList(), feedRange, partitionKeyPaths, jsonSettings);
        var filtered = CosmosQueryPipeline.ExecuteSafe(
            () => filterByQuery(queryDefinition.QueryText, parameters, requestOptions, preFiltered));
        var items = filtered.Select(json => JsonConvert.DeserializeObject<T>(json, jsonSettings)!).ToList();
        var initialOffset = DocumentValidator.ParseContinuationToken(continuationToken);
        return new InMemoryFeedIterator<T>(items, requestOptions?.MaxItemCount, initialOffset)
        {
            PopulateIndexMetrics = requestOptions?.PopulateIndexMetrics ?? false,
            GuaranteeFirstPage = true
        };
    }

    public static FeedIterator CreateStreamIterator(
        QueryDefinition queryDefinition,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        Func<string> getSessionToken)
    {
        var parameters = CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition);
        var filtered = CosmosQueryPipeline.ExecuteSafe(
            () => filterByQuery(queryDefinition.QueryText, parameters, requestOptions, null));
        return CosmosResponseFactory.CreateStreamFeedIterator(
            filtered, getSessionToken, DocumentValidator.ParseContinuationToken(continuationToken), requestOptions?.MaxItemCount);
    }

    public static FeedIterator CreateStreamIterator(
        string? queryText,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        Func<QueryRequestOptions?, IEnumerable<string>> getAllItemsForPartition,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        Func<string> getSessionToken)
    {
        var offset = DocumentValidator.ParseContinuationToken(continuationToken);
        if (string.IsNullOrEmpty(queryText))
        {
            return CosmosResponseFactory.CreateStreamFeedIterator(
                getAllItemsForPartition(requestOptions).ToList(), getSessionToken, offset, requestOptions?.MaxItemCount);
        }

        return CosmosResponseFactory.CreateStreamFeedIterator(
            CosmosQueryPipeline.ExecuteSafe(() => filterByQuery(queryText, new Dictionary<string, object?>(), requestOptions, null)),
            getSessionToken, offset, requestOptions?.MaxItemCount);
    }

    public static FeedIterator CreateStreamFeedRangeIterator(
        FeedRange feedRange,
        QueryDefinition queryDefinition,
        string? continuationToken,
        QueryRequestOptions? requestOptions,
        IReadOnlyList<string> partitionKeyPaths,
        Func<QueryRequestOptions?, IEnumerable<string>> getAllItemsForPartition,
        Func<string, IDictionary<string, object?>, QueryRequestOptions?, IEnumerable<string>?, List<string>> filterByQuery,
        JsonSerializerSettings jsonSettings,
        Func<string> getSessionToken)
    {
        var parameters = CosmosSdkIntrospector.ExtractQueryParameters(queryDefinition);
        var preFiltered = FeedRangeCalculator.FilterByFeedRange(
            getAllItemsForPartition(requestOptions).ToList(), feedRange, partitionKeyPaths, jsonSettings);
        var filtered = CosmosQueryPipeline.ExecuteSafe(
            () => filterByQuery(queryDefinition.QueryText, parameters, requestOptions, preFiltered));
        return CosmosResponseFactory.CreateStreamFeedIterator(
            filtered, getSessionToken, DocumentValidator.ParseContinuationToken(continuationToken), requestOptions?.MaxItemCount);
    }
}
