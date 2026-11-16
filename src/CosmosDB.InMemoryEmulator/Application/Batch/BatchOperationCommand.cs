#nullable enable
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace CosmosDB.InMemoryEmulator.Application.Batch;

internal enum BatchOpType
{
    Create,
    Read,
    Upsert,
    Replace,
    Delete,
    Patch
}

internal static class BatchOperationCommand
{
    public const int MaxBatchOperations = 100;
    public const int MaxBatchSizeBytes = 2 * 1024 * 1024;

    public static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        DateParseHandling = DateParseHandling.None,
        ContractResolver = new DefaultContractResolver(),
        Converters = { new StringEnumConverter { AllowIntegerValues = true } }
    };

    public static ItemRequestOptions? ToItemRequestOptions(TransactionalBatchItemRequestOptions? options)
    {
        if (options is null)
        {
            return null;
        }

        return new ItemRequestOptions
        {
            IfMatchEtag = options.IfMatchEtag,
            IfNoneMatchEtag = options.IfNoneMatchEtag,
            EnableContentResponseOnWrite = options.EnableContentResponseOnWrite,
            PreTriggers = ExtractTriggerHeader(options.Properties, "x-ms-pre-trigger-include"),
            PostTriggers = ExtractTriggerHeader(options.Properties, "x-ms-post-trigger-include"),
        };
    }

    public static PatchItemRequestOptions? ToPatchItemRequestOptions(TransactionalBatchPatchItemRequestOptions? requestOptions)
    {
        if (requestOptions is null)
        {
            return null;
        }

        return new PatchItemRequestOptions
        {
            IfMatchEtag = requestOptions.IfMatchEtag,
            IfNoneMatchEtag = requestOptions.IfNoneMatchEtag,
            FilterPredicate = requestOptions.FilterPredicate,
            PreTriggers = ExtractTriggerHeader(requestOptions.Properties, "x-ms-pre-trigger-include"),
            PostTriggers = ExtractTriggerHeader(requestOptions.Properties, "x-ms-post-trigger-include"),
        };
    }

    public static IEnumerable<string>? ExtractTriggerHeader(
        IReadOnlyDictionary<string, object>? properties,
        string headerName)
    {
        if (properties is not null
            && properties.TryGetValue(headerName, out var value)
            && value is IEnumerable<string> triggers)
        {
            return triggers;
        }

        return null;
    }

    public static async Task ExecuteStreamWriteAsync(
        string json,
        int opIndex,
        Func<MemoryStream, Task<ResponseMessage>> executeStreamAsync,
        Dictionary<int, string> writeEtags,
        Dictionary<int, string> readResults,
        Dictionary<int, HttpStatusCode>? opStatusCodes = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var response = await executeStreamAsync(stream);
        if (!response.IsSuccessStatusCode)
        {
            var subStatusCode = 0;
            if (int.TryParse(response.Headers["x-ms-substatus"], out var parsedSubStatus))
            {
                subStatusCode = parsedSubStatus;
            }

            throw InMemoryCosmosException.Create(
                response.ErrorMessage ?? "Stream operation failed.",
                response.StatusCode,
                subStatusCode,
                string.Empty,
                0);
        }

        writeEtags[opIndex] = response.Headers.ETag;
        if (opStatusCodes is not null)
        {
            opStatusCodes[opIndex] = response.StatusCode;
        }

        if (response.Content is not null)
        {
            using var responseReader = new StreamReader(response.Content);
            readResults[opIndex] = await responseReader.ReadToEndAsync();
        }
        else
        {
            readResults[opIndex] = json;
        }
    }
}
