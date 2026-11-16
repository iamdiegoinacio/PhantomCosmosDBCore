#nullable enable
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Phantom.CosmosDB.Core.Application.Batch;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Serialization.HybridRow;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.IO;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.Layouts;
using Microsoft.Azure.Cosmos.Serialization.HybridRow.RecordIO;
using HybridRowSchemas = Microsoft.Azure.Cosmos.Serialization.HybridRow.Schemas;

namespace Phantom.CosmosDB.Core.Infrastructure.HttpGateway;

/// <summary>
/// Encodes and decodes the Cosmos DB HybridRow / RecordIO binary wire format
/// for Transactional Batch HTTP requests and responses.
/// </summary>
internal static class HybridRowBatchCodec
{
    private static class BatchOperationTypes
    {
        public const int Create = 0;
        public const int Patch = 1;
        public const int Read = 2;
        public const int Delete = 4;
        public const int Replace = 5;
        public const int Upsert = 20;
    }

    private static class BatchSchemas
    {
        private static readonly Lazy<(Layout OperationLayout, Layout ResultLayout, LayoutResolverNamespace Resolver)> Schemas =
            new(BuildSchemas);

        public static Layout OperationLayout => Schemas.Value.OperationLayout;
        public static Layout ResultLayout => Schemas.Value.ResultLayout;
        public static LayoutResolverNamespace Resolver => Schemas.Value.Resolver;

        private static (Layout, Layout, LayoutResolverNamespace) BuildSchemas()
        {
            try
            {
                return BuildSchemasFromDefinition();
            }
            catch (Exception ex)
            {
                try
                {
                    return BuildSchemasFromReflection();
                }
                catch (Exception reflectionEx)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialise HybridRow batch schemas. " +
                        $"Self-built schema error: {ex.Message}. " +
                        $"Reflection fallback error: {reflectionEx.Message}. " +
                        $"This may indicate an incompatible Cosmos SDK version " +
                        $"({typeof(CosmosClient).Assembly.GetName().Version}). " +
                        $"Batch operations will not work.", ex);
                }
            }
        }

        private static (Layout, Layout, LayoutResolverNamespace) BuildSchemasFromDefinition()
        {
            var batchOperationSchema = new HybridRowSchemas.Schema
            {
                Name = "BatchOperation",
                SchemaId = new SchemaId(2145473648),
                Type = HybridRowSchemas.TypeKind.Schema,
                Properties =
                {
                    new HybridRowSchemas.Property { Path = "operationType", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Int32) { Storage = HybridRowSchemas.StorageKind.Fixed, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "resourceType", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Int32) { Storage = HybridRowSchemas.StorageKind.Fixed, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "partitionKey", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "effectivePartitionKey", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Binary) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "id", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "binaryId", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Binary) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "resourceBody", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Binary) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "indexingDirective", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "ifMatch", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "ifNoneMatch", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "timeToLiveInSeconds", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Int32) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "minimalReturnPreference", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Boolean) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                },
            };

            var batchResultSchema = new HybridRowSchemas.Schema
            {
                Name = "BatchResult",
                SchemaId = new SchemaId(2145473649),
                Type = HybridRowSchemas.TypeKind.Schema,
                Properties =
                {
                    new HybridRowSchemas.Property { Path = "statusCode", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Int32) { Storage = HybridRowSchemas.StorageKind.Fixed, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "subStatusCode", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Int32) { Storage = HybridRowSchemas.StorageKind.Fixed, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "eTag", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Utf8) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "resourceBody", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Binary) { Storage = HybridRowSchemas.StorageKind.Variable, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "retryAfterMilliseconds", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.UInt32) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                    new HybridRowSchemas.Property { Path = "requestCharge", PropertyType = new HybridRowSchemas.PrimitivePropertyType(HybridRowSchemas.TypeKind.Float64) { Storage = HybridRowSchemas.StorageKind.Sparse, Nullable = true } },
                },
            };

            var ns = new HybridRowSchemas.Namespace
            {
                Name = "Microsoft.Azure.Cosmos.BatchApi",
                Version = HybridRowSchemas.SchemaLanguageVersion.V2,
                Schemas = { batchOperationSchema, batchResultSchema },
            };

            var resolver = new LayoutResolverNamespace(ns, SystemSchema.LayoutResolver);
            var opLayout = resolver.Resolve(batchOperationSchema.SchemaId);
            var resultLayout = resolver.Resolve(batchResultSchema.SchemaId);
            return (opLayout, resultLayout, resolver);
        }

        private static (Layout, Layout, LayoutResolverNamespace) BuildSchemasFromReflection()
        {
            var bspType = typeof(CosmosClient).Assembly.GetType("Microsoft.Azure.Cosmos.BatchSchemaProvider")
                ?? throw new InvalidOperationException(
                    "Cannot find BatchSchemaProvider in the Cosmos SDK assembly. " +
                    "The SDK may have reorganised its internal types.");
            var flags = BindingFlags.Public | BindingFlags.Static;
            var opLayout = (Layout)(bspType.GetProperty("BatchOperationLayout", flags)?.GetValue(null)
                ?? throw new InvalidOperationException("BatchSchemaProvider.BatchOperationLayout not found."));
            var resultLayout = (Layout)(bspType.GetProperty("BatchResultLayout", flags)?.GetValue(null)
                ?? throw new InvalidOperationException("BatchSchemaProvider.BatchResultLayout not found."));
            var resolver = (LayoutResolverNamespace)(bspType.GetProperty("BatchLayoutResolver", flags)?.GetValue(null)
                ?? throw new InvalidOperationException("BatchSchemaProvider.BatchLayoutResolver not found."));
            return (opLayout, resultLayout, resolver);
        }
    }

    private record struct BatchOperation(
        int OperationType,
        string? Id,
        byte[]? ResourceBody,
        string? IfMatch,
        string? IfNoneMatch);

    public static bool BatchSchemasAvailable
    {
        get
        {
            _ = BatchSchemas.OperationLayout;
            return true;
        }
    }

    public static async Task<HttpResponseMessage> HandleBatchAsync(
        HttpRequestMessage request,
        InMemoryContainer container,
        IBatchSchemaStrategy batchSchemaStrategy,
        CancellationToken cancellationToken)
    {
        if (!batchSchemaStrategy.IsAvailable)
        {
            return new HttpResponseMessage(HttpStatusCode.NotImplemented)
            {
                Content = new StringContent(
                    $"{{\"message\":\"{(batchSchemaStrategy.UnavailableReason ?? "Batch schemas unavailable").Replace("\"", "\\\"")}\"}}",
                    Encoding.UTF8, "application/json")
            };
        }

        var pk = CosmosHttpHeaders.ExtractPartitionKey(request) ?? PartitionKey.None;

        var batchOps = new List<BatchOperation>();
        if (request.Content is not null)
        {
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bodyBytes.Length == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"message\":\"Empty batch request body.\"}", Encoding.UTF8, "application/json")
                };
            }

            using var bodyStream = new MemoryStream(bodyBytes);
            var batchLayoutResolver = BatchSchemas.Resolver;

            Func<ReadOnlyMemory<byte>, Result> recordVisitor = (ReadOnlyMemory<byte> record) =>
            {
                var row = new RowBuffer(record.Length);
                if (!row.ReadFrom(record.Span, HybridRowVersion.V1, batchLayoutResolver))
                    return Result.Failure;

                var reader = new RowReader(ref row);
                var opType = -1;
                string? id = null;
                byte[]? resourceBody = null;
                string? ifMatch = null;
                string? ifNoneMatch = null;

                while (reader.Read())
                {
                    switch (reader.Path)
                    {
                        case "operationType":
                            reader.ReadInt32(out int ot);
                            opType = ot;
                            break;
                        case "id":
                            reader.ReadString(out string idVal);
                            id = idVal;
                            break;
                        case "resourceBody":
                            reader.ReadBinary(out byte[] rb);
                            resourceBody = rb;
                            break;
                        case "ifMatch":
                            reader.ReadString(out string im);
                            ifMatch = im;
                            break;
                        case "ifNoneMatch":
                            reader.ReadString(out string inm);
                            ifNoneMatch = inm;
                            break;
                    }
                }

                batchOps.Add(new BatchOperation(opType, id, resourceBody, ifMatch, ifNoneMatch));
                return Result.Success;
            };

            var parseResult = await bodyStream.ReadRecordIOAsync(
                recordVisitor, resizer: new MemorySpanResizer<byte>(1024));

            if (parseResult != Result.Success)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        "{\"message\":\"Failed to parse batch request body.\"}",
                        Encoding.UTF8, "application/json")
                };
            }
        }

        if (batchOps.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"message\":\"Empty batch request.\"}", Encoding.UTF8, "application/json")
            };
        }

        var isAtomic = true;
        if (request.Headers.TryGetValues("x-ms-cosmos-batch-atomic", out var atomicValues))
        {
            var atomicStr = atomicValues.FirstOrDefault();
            if (string.Equals(atomicStr, "false", StringComparison.OrdinalIgnoreCase))
            {
                isAtomic = false;
            }
        }
        if (request.Headers.TryGetValues("x-ms-cosmos-batch-continue-on-error", out var continueValues))
        {
            var continueStr = continueValues.FirstOrDefault();
            if (string.Equals(continueStr, "true", StringComparison.OrdinalIgnoreCase))
            {
                isAtomic = false;
            }
        }

        var batch = container.CreateTransactionalBatch(pk);
        if (batch is InMemoryTransactionalBatch inMemoryBatch)
        {
            inMemoryBatch.IsAtomic = isAtomic;
        }

        foreach (var op in batchOps)
        {
            switch (op.OperationType)
            {
                case BatchOperationTypes.Create:
                    if (op.ResourceBody is not null)
                        batch.CreateItemStream(new MemoryStream(op.ResourceBody), BuildBatchItemRequestOptions(op));
                    break;
                case BatchOperationTypes.Upsert:
                    if (op.ResourceBody is not null)
                        batch.UpsertItemStream(new MemoryStream(op.ResourceBody), BuildBatchItemRequestOptions(op));
                    break;
                case BatchOperationTypes.Replace:
                    if (op.Id is not null && op.ResourceBody is not null)
                        batch.ReplaceItemStream(op.Id, new MemoryStream(op.ResourceBody), BuildBatchItemRequestOptions(op));
                    break;
                case BatchOperationTypes.Delete:
                    if (op.Id is not null)
                        batch.DeleteItem(op.Id, BuildBatchItemRequestOptions(op));
                    break;
                case BatchOperationTypes.Read:
                    if (op.Id is not null)
                        batch.ReadItem(op.Id, BuildBatchItemRequestOptions(op));
                    break;
                case BatchOperationTypes.Patch:
                    if (op.Id is not null && op.ResourceBody is not null)
                    {
                        var patchJson = Encoding.UTF8.GetString(op.ResourceBody);
                        var (patchOps, condition) = CosmosHttpHeaders.ParsePatchBody(patchJson);
                        var patchOptions = new TransactionalBatchPatchItemRequestOptions();
                        if (condition is not null) patchOptions.FilterPredicate = condition;
                        if (op.IfMatch is not null) patchOptions.IfMatchEtag = op.IfMatch;
                        batch.PatchItem(op.Id, patchOps, patchOptions);
                    }
                    break;
            }
        }

        var batchResponse = await batch.ExecuteAsync(cancellationToken);
        var inMemoryBatchResponse = batchResponse as InMemoryBatchResponse;

        var batchResultLayout = BatchSchemas.ResultLayout;
        var batchLayoutResolverForResponse = BatchSchemas.Resolver;

        using var responseStream = new MemoryStream();
        var resizer = new MemorySpanResizer<byte>(256);

        await RecordIOStream.WriteRecordIOAsync(
            responseStream,
            default(Segment),
            (long index, out ReadOnlyMemory<byte> buffer) =>
            {
                if (index >= batchResponse.Count)
                {
                    buffer = default;
                    return Result.Success;
                }

                var opResult = batchResponse[(int)index];
                var opSubStatusCode = inMemoryBatchResponse?.GetOperationSubStatusCode((int)index) ?? 0;
                var row = new RowBuffer(256, resizer);
                row.InitLayout(HybridRowVersion.V1, batchResultLayout, batchLayoutResolverForResponse);
                var r = RowWriter.WriteBuffer(ref row, (opResult, opSubStatusCode), (ref RowWriter writer, TypeArgument _, (TransactionalBatchOperationResult result, int subStatus) ctx) =>
                {
                    var wr = writer.WriteInt32("statusCode", (int)ctx.result.StatusCode);
                    if (wr != Result.Success) return wr;

                    if (ctx.subStatus != 0)
                    {
                        wr = writer.WriteInt32("subStatusCode", ctx.subStatus);
                        if (wr != Result.Success) return wr;
                    }

                    if (ctx.result.ETag is not null)
                    {
                        wr = writer.WriteString("eTag", ctx.result.ETag);
                        if (wr != Result.Success) return wr;
                    }

                    if (ctx.result.ResourceStream is not null)
                    {
                        using var ms = new MemoryStream();
                        ctx.result.ResourceStream.CopyTo(ms);
                        ctx.result.ResourceStream.Position = 0;
                        wr = writer.WriteBinary("resourceBody", ms.ToArray());
                        if (wr != Result.Success) return wr;
                    }

                    wr = writer.WriteFloat64("requestCharge", 1.0);
                    if (wr != Result.Success) return wr;

                    return Result.Success;
                });

                if (r != Result.Success)
                {
                    buffer = default;
                    return r;
                }

                buffer = resizer.Memory.Slice(0, row.Length);
                return Result.Success;
            },
            new MemorySpanResizer<byte>(128));

        var httpResponse = new HttpResponseMessage(batchResponse.StatusCode)
        {
            Content = new ByteArrayContent(responseStream.ToArray())
        };
        httpResponse.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        httpResponse.Headers.Add("x-ms-request-charge", "1");
        httpResponse.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        httpResponse.Headers.Add("x-ms-session-token", container.CurrentSessionToken);
        if (inMemoryBatchResponse is { BatchSubStatusCode: not 0 })
        {
            httpResponse.Headers.Add("x-ms-substatus", inMemoryBatchResponse.BatchSubStatusCode.ToString(CultureInfo.InvariantCulture));
        }
        return httpResponse;
    }

    private static TransactionalBatchItemRequestOptions? BuildBatchItemRequestOptions(BatchOperation op)
    {
        if (op.IfMatch is null && op.IfNoneMatch is null)
            return null;
        return new TransactionalBatchItemRequestOptions
        {
            IfMatchEtag = op.IfMatch,
            IfNoneMatchEtag = op.IfNoneMatch
        };
    }
}
