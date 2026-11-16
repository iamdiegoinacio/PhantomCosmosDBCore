#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using Phantom.CosmosDB.Core.Infrastructure.FeedIterators;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NSubstitute;

namespace Phantom.CosmosDB.Core.Application.Batch;

/// <summary>
/// In-memory implementation of <see cref="TransactionalBatchResponse"/>.
/// </summary>
internal sealed class InMemoryBatchResponse : TransactionalBatchResponse
{
    private readonly HttpStatusCode _statusCode;
    private readonly bool _isSuccess;
    private readonly List<(HttpStatusCode status, bool isSuccess)> _operationResults;
    private readonly Dictionary<int, string> _readResults;
    private readonly Dictionary<int, string> _writeEtags;
    private readonly Dictionary<int, int> _opSubStatusCodes;
    private readonly string? _errorMessage;
    private readonly Headers _headers = new();

    public InMemoryBatchResponse(
        HttpStatusCode statusCode,
        bool isSuccess,
        List<(HttpStatusCode status, bool isSuccess)> operationResults,
        Dictionary<int, string> readResults,
        Dictionary<int, string> writeEtags,
        string? errorMessage = null,
        string? sessionToken = null,
        int subStatusCode = 0,
        Dictionary<int, int>? opSubStatusCodes = null)
    {
        _statusCode = statusCode;
        _isSuccess = isSuccess;
        _operationResults = operationResults;
        _readResults = readResults;
        _writeEtags = writeEtags;
        _opSubStatusCodes = opSubStatusCodes ?? new Dictionary<int, int>();
        _errorMessage = errorMessage;
        BatchSubStatusCode = subStatusCode;
        _headers["x-ms-activity-id"] = ActivityId;
        _headers["x-ms-request-charge"] = RequestCharge.ToString(CultureInfo.InvariantCulture);
        _headers["x-ms-session-token"] = sessionToken ?? "0:0#0";
        if (subStatusCode != 0)
        {
            _headers["x-ms-substatus"] = subStatusCode.ToString(CultureInfo.InvariantCulture);
        }
    }

    public int BatchSubStatusCode { get; }

    public int GetOperationSubStatusCode(int index)
        => _opSubStatusCodes.TryGetValue(index, out var subStatus) ? subStatus : 0;

    public override HttpStatusCode StatusCode => _statusCode;
    public override bool IsSuccessStatusCode => _isSuccess;
    public override int Count => _operationResults.Count;
    public override double RequestCharge => 1d;
    public override string ActivityId { get; } = Guid.NewGuid().ToString();
    public override CosmosDiagnostics Diagnostics => InMemoryFeedDiagnostics.Instance;
    public override Headers Headers => _headers;
    public override string? ErrorMessage => _errorMessage;
    public override TimeSpan? RetryAfter => TimeSpan.Zero;

    public override TransactionalBatchOperationResult this[int index]
    {
        get
        {
            if (index < 0 || index >= _operationResults.Count) return null!;
            var (status, isSuccess) = _operationResults[index];
            var result = Substitute.For<TransactionalBatchOperationResult>();
            result.StatusCode.Returns(status);
            result.IsSuccessStatusCode.Returns(isSuccess);
            if (_writeEtags.TryGetValue(index, out var etag))
            {
                result.ETag.Returns(etag);
            }
            if (_readResults.TryGetValue(index, out var json))
            {
                result.ResourceStream.Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes(json)));
            }
            return result;
        }
    }

    public override TransactionalBatchOperationResult<T> GetOperationResultAtIndex<T>(int index)
    {
        var (status, isSuccess) = _operationResults[index];
        var result = Substitute.For<TransactionalBatchOperationResult<T>>();
        result.StatusCode.Returns(status);
        result.IsSuccessStatusCode.Returns(isSuccess);

        if (_readResults.TryGetValue(index, out var json))
        {
            result.Resource.Returns(JsonConvert.DeserializeObject<T>(json, BatchOperationCommand.JsonSettings)!);
        }

        if (_writeEtags.TryGetValue(index, out var etag))
        {
            result.ETag.Returns(etag);
        }

        return result;
    }

    public override IEnumerator<TransactionalBatchOperationResult> GetEnumerator()
    {
        for (var i = 0; i < _operationResults.Count; i++)
        {
            yield return this[i];
        }
    }

    protected override void Dispose(bool disposing) { }
}
