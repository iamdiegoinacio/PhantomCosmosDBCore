#nullable enable
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Infrastructure.HttpGateway;

/// <summary>
/// Parses Cosmos DB HTTP Gateway headers, continuation tokens, document routes, and patch bodies,
/// and constructs standard HTTP responses.
/// </summary>
internal static class CosmosHttpHeaders
{
    /// <summary>
    /// Set of <c>x-ms-*</c> request headers that the gateway handler recognises and processes.
    /// Any <c>x-ms-*</c> header not in this set is recorded in <see cref="FakeCosmosHandler.UnrecognisedHeaders"/>.
    /// </summary>
    public static readonly HashSet<string> KnownRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "A-IM",
        "x-ms-documentdb-partitionkey",
        "x-ms-documentdb-isquery",
        "x-ms-documentdb-is-upsert",
        "x-ms-cosmos-is-batch-request",
        "x-ms-cosmos-is-query-plan-request",
        "x-ms-max-item-count",
        "x-ms-continuation",
        "x-ms-documentdb-partitionkeyrangeid",
        "x-ms-date",
        "x-ms-version",
        "x-ms-documentdb-query-enablecrosspartition",
        "x-ms-documentdb-query-iscontinuationexpected",
        "x-ms-documentdb-query-parallelizecrosspartitionquery",
        "x-ms-documentdb-populatequerymetrics",
        "x-ms-cosmos-correlated-activityid",
        "x-ms-activity-id",
        "x-ms-cosmos-sdk-supportedcapabilities",
        "x-ms-session-token",
        "x-ms-consistency-level",
        "x-ms-request-charge",
        "x-ms-cosmos-internal-is-query",
        "x-ms-cosmos-query-version",
        "x-ms-documentdb-collection-rid",
        "x-ms-cosmos-batch-ordered-response",
        "x-ms-cosmos-batch-atomic",
        "x-ms-cosmos-batch-continue-on-error",
        "x-ms-cosmos-sdk-version",
        "x-ms-cosmos-intended-collection-rid",
        "x-ms-cosmos-priority-level",
        "x-ms-cosmos-allow-tentative-writes",
        "x-ms-cosmos-physical-partition-count",
        "x-ms-documentdb-responsecontinuationtokenlimitinkb",
        "x-ms-documentdb-content-serialization-format",
        "x-ms-cosmos-query-optimisticdirectexecute",
        "x-ms-cosmos-supported-serialization-formats",
        "x-ms-cosmos-supported-query-features",
        "x-ms-documentdb-pre-trigger-include",
        "x-ms-documentdb-post-trigger-include",
        "x-ms-documentdb-script-enable-logging",
        "x-ms-offer-throughput",
    };

    public static string ExtractDocumentId(HttpRequestMessage request)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        var docsIndex = path.LastIndexOf("/docs/", StringComparison.OrdinalIgnoreCase);
        if (docsIndex >= 0)
        {
            var id = path[(docsIndex + 6)..];
            return Uri.UnescapeDataString(id.TrimEnd('/'));
        }
        return "";
    }

    public static bool HasDocumentId(string path)
    {
        var docsIndex = path.LastIndexOf("/docs/", StringComparison.OrdinalIgnoreCase);
        if (docsIndex < 0) return false;
        var afterDocs = path[(docsIndex + 6)..].TrimEnd('/');
        return afterDocs.Length > 0;
    }

    public static ItemRequestOptions BuildItemRequestOptions(HttpRequestMessage request)
    {
        var options = new ItemRequestOptions();
        if (request.Headers.IfMatch.Any())
        {
            options.IfMatchEtag = request.Headers.IfMatch.First().Tag;
        }
        if (request.Headers.IfNoneMatch.Any())
        {
            options.IfNoneMatchEtag = request.Headers.IfNoneMatch.First().Tag;
        }

        var preTriggers = ReadCommaSeparatedHeader(request, "x-ms-documentdb-pre-trigger-include");
        if (preTriggers.Count > 0)
        {
            options.PreTriggers = preTriggers;
        }

        var postTriggers = ReadCommaSeparatedHeader(request, "x-ms-documentdb-post-trigger-include");
        if (postTriggers.Count > 0)
        {
            options.PostTriggers = postTriggers;
        }

        return options;
    }

    private static List<string> ReadCommaSeparatedHeader(HttpRequestMessage request, string headerName)
    {
        var result = new List<string>();
        if (request.Headers.TryGetValues(headerName, out var values))
        {
            foreach (var value in values)
            {
                result.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        }

        return result;
    }

    public static bool IsQueryRequest(HttpRequestMessage request)
    {
        var contentType = request.Content?.Headers?.ContentType?.MediaType ?? "";
        if (contentType.Contains("query+json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (request.Headers.TryGetValues("x-ms-documentdb-isquery", out var values) &&
            values.Any(v => v.Equals("True", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    public static bool IsUpsertRequest(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("x-ms-documentdb-is-upsert", out var values) &&
               values.Any(v => v.Equals("True", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsBatchRequest(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("x-ms-cosmos-is-batch-request", out var values) &&
               values.Any(v => v.Equals("True", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsQueryPlanRequest(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("x-ms-cosmos-is-query-plan-request", out var qpValues) &&
               qpValues.Any(v => v.Equals("True", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsChangeFeedRequest(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("A-IM", out var values))
        {
            return false;
        }
        return values.Any(v => v.Contains("Incremental", StringComparison.OrdinalIgnoreCase)
                               || v.Contains("Full-Fidelity", StringComparison.OrdinalIgnoreCase)
                               || v.Contains("AllVersionsAndDeletes", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsFullFidelityChangeFeedRequest(HttpRequestMessage request)
    {
        return request.Headers.TryGetValues("A-IM", out var values)
               && values.Any(v => v.Contains("Full-Fidelity", StringComparison.OrdinalIgnoreCase)
                                  || v.Contains("AllVersionsAndDeletes", StringComparison.OrdinalIgnoreCase));
    }

    public static (IReadOnlyList<PatchOperation> Operations, string? Condition) ParsePatchBody(string body)
    {
        var jObj = JObject.Parse(body);
        var operations = new List<PatchOperation>();
        var condition = jObj["condition"]?.ToString();

        var opsToken = jObj["operations"];
        if (opsToken is null)
            throw new InvalidOperationException("Patch body missing 'operations' array.");

        foreach (var op in opsToken.ToObject<JArray>()!)
        {
            var opType = op["op"]!.ToString().ToLowerInvariant();
            var opPath = op["path"]!.ToString();
            var value = op["value"];

            operations.Add(opType switch
            {
                "set" => PatchOperation.Set(opPath, value),
                "replace" => PatchOperation.Replace(opPath, value),
                "add" => PatchOperation.Add(opPath, value),
                "remove" => PatchOperation.Remove(opPath),
                "incr" => PatchOperation.Increment(opPath, value!.Value<double>()),
                "move" => PatchOperation.Move(op["from"]!.ToString(), opPath),
                _ => throw new InvalidOperationException($"Unknown patch operation: {opType}")
            });
        }

        return (operations, condition);
    }

    public static PartitionKey? ExtractPartitionKey(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues("x-ms-documentdb-partitionkey", out var values))
        {
            var raw = values.FirstOrDefault();
            if (raw is not null)
            {
                try
                {
                    var arr = JArray.Parse(raw);
                    if (arr.Count == 1)
                    {
                        return arr[0].Type switch
                        {
                            JTokenType.String => new PartitionKey(arr[0].Value<string>()),
                            JTokenType.Integer or JTokenType.Float => new PartitionKey(arr[0].Value<double>()),
                            JTokenType.Boolean => new PartitionKey(arr[0].Value<bool>()),
                            JTokenType.Null => PartitionKey.Null,
                            JTokenType.Object => PartitionKey.None, // SDK sends [{}] for PartitionKey.None
                            _ => new PartitionKey(arr[0].ToString())
                        };
                    }

                    if (arr.Count > 1)
                    {
                        var builder = new PartitionKeyBuilder();
                        foreach (var token in arr)
                        {
                            switch (token.Type)
                            {
                                case JTokenType.String:
                                    builder.Add(token.Value<string>()!);
                                    break;
                                case JTokenType.Integer or JTokenType.Float:
                                    builder.Add(token.Value<double>());
                                    break;
                                case JTokenType.Boolean:
                                    builder.Add(token.Value<bool>());
                                    break;
                                case JTokenType.Null:
                                    builder.AddNullValue();
                                    break;
                                default:
                                    builder.Add(token.ToString());
                                    break;
                            }
                        }
                        return builder.Build();
                    }
                }
                catch
                {
                    // Ignore malformed partition key headers
                }
            }
        }

        return null;
    }

    public static int ExtractMaxItemCount(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues("x-ms-max-item-count", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var count) && count > 0)
        {
            return count;
        }

        return 0;
    }

    public static string? ExtractPartitionKeyRangeId(HttpRequestMessage request)
    {
        if (request.Headers.TryGetValues("x-ms-documentdb-partitionkeyrangeid", out var values))
        {
            return values.FirstOrDefault();
        }

        return null;
    }

    public static (string Key, int Offset)? DecodeContinuation(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("x-ms-continuation", out var values))
        {
            return null;
        }

        var raw = values.FirstOrDefault();
        if (raw is null)
        {
            return null;
        }

        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            var obj = JsonParseHelpers.ParseJson(json);
            var key = obj["key"]?.Value<string>();
            var offset = obj["offset"]?.Value<int>() ?? 0;
            return key is not null ? (key, offset) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string EncodeContinuation(string cacheKey, int offset)
    {
        var json = $"{{\"v\":2,\"key\":\"{cacheKey}\",\"offset\":{offset}}}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    internal enum ChargeKind
    {
        Flat,
        Read,
        Write,
    }

    public static HttpResponseMessage ConvertToHttpResponse(
        ResponseMessage cosmosResponse,
        string sessionToken,
        ChargeKind chargeKind = ChargeKind.Flat)
    {
        var httpResponse = new HttpResponseMessage(cosmosResponse.StatusCode);
        long payloadBytes = 0;
        if (cosmosResponse.Content is not null)
        {
            using var reader = new StreamReader(cosmosResponse.Content);
            var json = reader.ReadToEnd();
            if (json.Length > 0)
            {
                payloadBytes = Encoding.UTF8.GetByteCount(json);
                httpResponse.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
        }

        var subStatus = cosmosResponse.Headers["x-ms-substatus"];

        if (httpResponse.Content is null && (int)cosmosResponse.StatusCode >= 400)
        {
            var message = cosmosResponse.ErrorMessage ?? cosmosResponse.StatusCode.ToString();
            httpResponse.Content = new StringContent(
                BuildErrorBody(cosmosResponse.StatusCode, message), Encoding.UTF8, "application/json");
        }

        var charge = chargeKind switch
        {
            ChargeKind.Read when payloadBytes > 0 => CosmosDB.InMemoryEmulator.Domain.Primitives.RequestChargeCalculator.PointRead(payloadBytes),
            ChargeKind.Write => CosmosDB.InMemoryEmulator.Domain.Primitives.RequestChargeCalculator.Write(payloadBytes),
            _ => 1d
        };
        httpResponse.Headers.Add("x-ms-request-charge", charge.ToString(System.Globalization.CultureInfo.InvariantCulture));
        httpResponse.Headers.Add("x-ms-activity-id", cosmosResponse.Headers["x-ms-activity-id"] ?? Guid.NewGuid().ToString());
        httpResponse.Headers.Add("x-ms-session-token", sessionToken);

        if (subStatus is not null)
        {
            httpResponse.Headers.Add("x-ms-substatus", subStatus);
        }

        var etag = cosmosResponse.Headers["ETag"];
        if (etag is not null)
        {
            httpResponse.Headers.TryAddWithoutValidation("etag", etag);
        }

        return httpResponse;
    }

    /// <summary>
    /// Builds the standard Cosmos DB error envelope: <c>{"code":"NotFound","message":"..."}</c>.
    /// </summary>
    public static string BuildErrorBody(HttpStatusCode statusCode, string message)
    {
        var body = new JObject
        {
            ["code"] = statusCode.ToString(),
            ["message"] = message
        };
        return body.ToString(Newtonsoft.Json.Formatting.None);
    }

    public static HttpResponseMessage CreateErrorResponse(
        HttpStatusCode statusCode,
        string message,
        int subStatusCode,
        string sessionToken)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(BuildErrorBody(statusCode, message), Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-ms-request-charge", "1");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-session-token", sessionToken);
        if (subStatusCode != 0)
        {
            response.Headers.Add("x-ms-substatus", subStatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }

    public static HttpResponseMessage CreateJsonResponse(string json, string sessionToken, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-ms-request-charge", "0");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        response.Headers.Add("x-ms-session-token", sessionToken);
        return response;
    }

    public static HttpResponseMessage CreateNoContentResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.NoContent);
        response.Headers.Add("x-ms-request-charge", "0");
        response.Headers.Add("x-ms-activity-id", Guid.NewGuid().ToString());
        return response;
    }
}
