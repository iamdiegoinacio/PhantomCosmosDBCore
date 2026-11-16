#nullable enable
using System.Diagnostics;
using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace Phantom.CosmosDB.Core.Infrastructure.Diagnostics;

/// <summary>
/// Executes end-to-end canary checks against the current <see cref="CosmosClient"/> SDK version
/// and <see cref="FakeCosmosHandler"/> to detect breaking wire-protocol changes early.
/// </summary>
internal static class SdkCompatibilityVerifier
{
    public static async Task VerifyAsync()
    {
        var sdkVersion = typeof(CosmosClient).Assembly.GetName().Version?.ToString() ?? "unknown";

        var container = new InMemoryContainer("compat-check", "/partitionKey");
        await container.CreateItemAsync(
            new CompatibilityDocument { Id = "1", PartitionKey = "pk", Name = "Alice", Value = 10 },
            new PartitionKey("pk"));
        await container.CreateItemAsync(
            new CompatibilityDocument { Id = "2", PartitionKey = "pk", Name = "Bob", Value = 20 },
            new PartitionKey("pk"));
        await container.CreateItemAsync(
            new CompatibilityDocument { Id = "3", PartitionKey = "pk", Name = "Charlie", Value = 30 },
            new PartitionKey("pk"));

        using var handler = new FakeCosmosHandler(container);
        using var client = new CosmosClient(
            FakeCosmosHandler.FakeConnectionString,
            new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                MaxRetryAttemptsOnRateLimitedRequests = 0,
                RequestTimeout = TimeSpan.FromSeconds(10),
                HttpClientFactory = () => new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }
            });

        var cosmosContainer = client.GetContainer("fakeDb", "compat-check");

        var allItems = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemLinqQueryable<CompatibilityDocument>().ToFeedIterator());
        if (allItems.Count != 3)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): expected 3 items from basic query but got {allItems.Count}. " +
                "The Cosmos SDK may have changed its internal HTTP contract.");
        }

        var ordered = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemLinqQueryable<CompatibilityDocument>()
                .OrderBy(document => document.Name)
                .ToFeedIterator());
        if (ordered.Count != 3 || ordered[0].Name != "Alice" || ordered[2].Name != "Charlie")
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): ORDER BY query returned unexpected results. " +
                "The Cosmos SDK ORDER BY response format may have changed.");
        }

        var filtered = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemLinqQueryable<CompatibilityDocument>()
                .Where(document => document.Value > 15)
                .ToFeedIterator());
        if (filtered.Count != 2)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): expected 2 items from filtered query but got {filtered.Count}. " +
                "The Cosmos SDK may have changed its query or header format.");
        }

        var paginatedItems = new List<CompatibilityDocument>();
        var pageIterator = cosmosContainer.GetItemLinqQueryable<CompatibilityDocument>(
                requestOptions: new QueryRequestOptions { MaxItemCount = 1 })
            .ToFeedIterator();
        var pageCount = 0;
        while (pageIterator.HasMoreResults)
        {
            var page = await pageIterator.ReadNextAsync();
            paginatedItems.AddRange(page);
            pageCount++;
        }

        if (paginatedItems.Count != 3)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): paginated query returned {paginatedItems.Count} items instead of 3. " +
                "The Cosmos SDK may have changed its pagination or continuation token format.");
        }

        if (pageCount < 2)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): expected multiple pages with MaxItemCount=1 but got {pageCount} page(s). " +
                "The Cosmos SDK may have changed how it sends the x-ms-max-item-count header.");
        }

        var countResult = await cosmosContainer.GetItemLinqQueryable<CompatibilityDocument>().CountAsync();
        if (countResult.Resource != 3)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): CountAsync returned {countResult.Resource} instead of 3. " +
                "The Cosmos SDK may have changed its aggregate query format.");
        }

        if (!handler.RequestLog.Any(entry => entry.Contains("/pkranges")))
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): no partition key range request was detected. " +
                "The Cosmos SDK may have changed how it discovers partition key ranges.");
        }

        if (!handler.QueryLog.Any())
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): no query was logged. " +
                "The Cosmos SDK may have changed how it sends query requests.");
        }

        // CRUD roundtrip
        var crudDoc = new CompatibilityDocument { Id = "crud-test", PartitionKey = "pk", Name = "CrudTest", Value = 99 };
        var createResponse = await cosmosContainer.CreateItemAsync(crudDoc, new PartitionKey("pk"));
        if (createResponse.StatusCode != HttpStatusCode.Created)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): CreateItemAsync returned {createResponse.StatusCode} instead of Created. " +
                "The Cosmos SDK may have changed its CRUD HTTP contract.");
        }

        var readResponse = await cosmosContainer.ReadItemAsync<CompatibilityDocument>("crud-test", new PartitionKey("pk"));
        if (readResponse.Resource?.Name != "CrudTest")
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): ReadItemAsync returned unexpected resource. " +
                "The Cosmos SDK may have changed its point-read HTTP contract.");
        }

        var deleteResponse = await cosmosContainer.DeleteItemAsync<CompatibilityDocument>("crud-test", new PartitionKey("pk"));
        if (deleteResponse.StatusCode != HttpStatusCode.NoContent)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): DeleteItemAsync returned {deleteResponse.StatusCode} instead of NoContent. " +
                "The Cosmos SDK may have changed its delete HTTP contract.");
        }

        // DISTINCT query
        var distinctItems = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemQueryIterator<dynamic>(
                new QueryDefinition("SELECT DISTINCT c.name FROM c")));
        if (distinctItems.Count != 3)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): DISTINCT query returned {distinctItems.Count} items instead of 3. " +
                "The Cosmos SDK may have changed its DISTINCT query handling.");
        }

        // OFFSET/LIMIT query
        var offsetLimitItems = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemQueryIterator<CompatibilityDocument>(
                new QueryDefinition("SELECT * FROM c OFFSET 1 LIMIT 2")));
        if (offsetLimitItems.Count != 2)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): OFFSET/LIMIT query returned {offsetLimitItems.Count} items instead of 2. " +
                "The Cosmos SDK may have changed its OFFSET/LIMIT handling.");
        }

        // Upsert roundtrip
        var upsertDoc = new CompatibilityDocument { Id = "upsert-test", PartitionKey = "pk", Name = "Upserted", Value = 42 };
        var upsertResponse = await cosmosContainer.UpsertItemAsync(upsertDoc, new PartitionKey("pk"));
        if (upsertResponse.StatusCode is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): UpsertItemAsync returned {upsertResponse.StatusCode}. " +
                "The Cosmos SDK may have changed its upsert HTTP contract.");
        }

        // Patch roundtrip
        var patchOps = new[] { PatchOperation.Set("/value", 99) };
        var patchResponse = await cosmosContainer.PatchItemAsync<CompatibilityDocument>(
            "upsert-test", new PartitionKey("pk"), patchOps);
        if (patchResponse.Resource?.Value != 99)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): PatchItemAsync returned unexpected value. " +
                "The Cosmos SDK may have changed its patch HTTP contract.");
        }

        await cosmosContainer.DeleteItemAsync<CompatibilityDocument>("upsert-test", new PartitionKey("pk"));

        // Transactional batch roundtrip
        var batchDoc1 = new CompatibilityDocument { Id = "batch-1", PartitionKey = "pk", Name = "B1", Value = 1 };
        var batchDoc2 = new CompatibilityDocument { Id = "batch-2", PartitionKey = "pk", Name = "B2", Value = 2 };
        var batchResponse = await cosmosContainer.CreateTransactionalBatch(new PartitionKey("pk"))
            .CreateItem(batchDoc1)
            .CreateItem(batchDoc2)
            .ExecuteAsync();

        if (!batchResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): TransactionalBatch returned " +
                $"{batchResponse.StatusCode}. The Cosmos SDK may have changed its batch wire format " +
                "(HybridRow schemas or RecordIO encoding).");
        }

        if (batchResponse.Count != 2)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): batch returned {batchResponse.Count} " +
                "results instead of 2.");
        }

        await cosmosContainer.DeleteItemAsync<CompatibilityDocument>("batch-1", new PartitionKey("pk"));
        await cosmosContainer.DeleteItemAsync<CompatibilityDocument>("batch-2", new PartitionKey("pk"));

        // ReadMany roundtrip
        var readManyResult = await cosmosContainer.ReadManyItemsAsync<CompatibilityDocument>(
            new[] { ("1", new PartitionKey("pk")), ("2", new PartitionKey("pk")) });

        if (readManyResult.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): ReadManyItemsAsync returned " +
                $"{readManyResult.StatusCode}. The Cosmos SDK may have changed its ReadMany HTTP contract.");
        }

        if (readManyResult.Count != 2)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): ReadMany returned {readManyResult.Count} " +
                "items instead of 2.");
        }

        // Change feed roundtrip
        var changeFeedIterator = container.GetChangeFeedIterator<CompatibilityDocument>(0);
        var changeFeedItems = new List<CompatibilityDocument>();
        while (changeFeedIterator.HasMoreResults)
        {
            var page = await changeFeedIterator.ReadNextAsync();
            changeFeedItems.AddRange(page);
        }

        if (changeFeedItems.Count < 3)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): Change feed returned {changeFeedItems.Count} " +
                "items instead of at least 3. The change feed infrastructure may be broken.");
        }

        // GROUP BY query
        var groupByItems = await DrainFeedIteratorAsync(
            cosmosContainer.GetItemQueryIterator<dynamic>(
                new QueryDefinition("SELECT c.partitionKey, COUNT(1) AS cnt FROM c GROUP BY c.partitionKey")));

        if (groupByItems.Count < 1)
        {
            throw new InvalidOperationException(
                $"SDK compatibility check failed (v{sdkVersion}): GROUP BY query returned no results. " +
                "The Cosmos SDK may have changed its GROUP BY handling.");
        }

        if (handler.UnrecognisedHeaders.Count > 0)
        {
            var unknownHeaders = string.Join(", ", handler.UnrecognisedHeaders.Distinct());
            Trace.TraceWarning(
                $"FakeCosmosHandler: SDK v{sdkVersion} sent unrecognised x-ms-* headers: {unknownHeaders}. " +
                "These may indicate new SDK features that are not yet handled.");
        }
    }

    private static async Task<List<T>> DrainFeedIteratorAsync<T>(FeedIterator<T> iterator)
    {
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync();
            items.AddRange(page);
        }

        return items;
    }
}
