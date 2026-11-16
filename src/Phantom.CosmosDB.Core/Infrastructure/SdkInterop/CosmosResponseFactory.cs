#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NSubstitute;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;

namespace Phantom.CosmosDB.Core.Infrastructure.SdkInterop;

/// <summary>
/// Factory for creating strongly-typed and stream Cosmos SDK responses and iterators.
/// </summary>
internal static class CosmosResponseFactory
{
    public static readonly CosmosDiagnostics FakeDiagnostics = new InMemoryDiagnostics();

    public static string ReadStream(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, true);
        return reader.ReadToEnd();
    }

    public static MemoryStream ToStream(string json) => new(Encoding.UTF8.GetBytes(json));

    public static ItemResponse<T> CreateItemResponse<T>(
        T item,
        HttpStatusCode statusCode,
        string sessionToken,
        string? etag = null,
        bool suppressContent = false)
    {
        var activityId = Guid.NewGuid().ToString();
        var headers = new Headers
        {
            ["x-ms-session-token"] = sessionToken,
            ["x-ms-activity-id"] = activityId,
            ["x-ms-request-charge"] = SyntheticRequestCharge.ToString(CultureInfo.InvariantCulture)
        };
        return new InMemoryItemResponse<T>(
            statusCode, headers, suppressContent ? default! : item, FakeDiagnostics,
            SyntheticRequestCharge, activityId, etag);
    }

    public static ResponseMessage CreateResponseMessage(
        HttpStatusCode statusCode,
        string sessionToken,
        string? json = null,
        string? etag = null,
        int subStatusCode = 0)
    {
        // Ref: https://learn.microsoft.com/en-us/rest/api/cosmos-db/http-status-codes-for-cosmosdb
        var errorMessage = statusCode switch
        {
            HttpStatusCode.NotFound => "Resource Not Found. Entity with the specified id does not exist in the system.",
            _ when (int)statusCode >= 400 => $"Response status code does not indicate success: {statusCode} ({(int)statusCode})",
            _ => null
        };

        // For error responses with no explicit body, synthesize a JSON error body matching the
        // real Cosmos DB REST API format. The SDK reads this body to populate CosmosException.ResponseBody.
        if (json is null && errorMessage is not null)
        {
            json = $"{{\"code\":\"{statusCode}\",\"message\":\"{errorMessage}\"}}";
        }

        var msg = new ResponseMessage(statusCode, requestMessage: null, errorMessage: errorMessage)
        {
            Content = json is not null ? ToStream(json) : null
        };
        msg.Headers["x-ms-activity-id"] = Guid.NewGuid().ToString();
        msg.Headers["x-ms-request-charge"] = SyntheticRequestCharge.ToString(CultureInfo.InvariantCulture);
        msg.Headers["x-ms-session-token"] = sessionToken;
        if (subStatusCode != 0)
        {
            msg.Headers["x-ms-substatus"] = subStatusCode.ToString(CultureInfo.InvariantCulture);
        }
        if (etag is not null)
        {
            msg.Headers["ETag"] = etag;
        }

        return msg;
    }

    public static FeedResponse<T> CreateFeedResponse<T>(
        IReadOnlyList<T> items,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? etag = null)
        => new InMemoryFeedResponse<T>(items, statusCode, etag);

    public static ContainerResponse CreateContainerResponse(
        Container container,
        ContainerProperties? properties,
        HttpStatusCode statusCode)
    {
        var r = Substitute.For<ContainerResponse>();
        r.StatusCode.Returns(statusCode);
        if (properties is not null)
        {
            r.Resource.Returns(properties);
        }
        r.Container.Returns(container);
        return r;
    }

    public static DatabaseResponse CreateDatabaseResponse(
        Database database,
        HttpStatusCode statusCode,
        DatabaseProperties? properties = null)
    {
        var r = Substitute.For<DatabaseResponse>();
        r.Database.Returns(database);
        r.StatusCode.Returns(statusCode);
        if (properties is not null)
        {
            r.Resource.Returns(properties);
        }
        return r;
    }

    public static UserResponse CreateUserResponse(
        User user,
        UserProperties? properties,
        HttpStatusCode statusCode)
    {
        var r = Substitute.For<UserResponse>();
        r.StatusCode.Returns(statusCode);
        r.Resource.Returns(properties!);
        r.User.Returns(user);
        return r;
    }

    public static PermissionResponse CreatePermissionResponse(
        Permission permission,
        PermissionProperties? properties,
        HttpStatusCode statusCode)
    {
        var r = Substitute.For<PermissionResponse>();
        r.StatusCode.Returns(statusCode);
        r.Resource.Returns(properties!);
        r.Permission.Returns(permission);
        return r;
    }

    public static ThroughputResponse CreateThroughputResponse(
        ThroughputProperties properties,
        HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var r = Substitute.For<ThroughputResponse>();
        r.StatusCode.Returns(statusCode);
        r.Resource.Returns(properties);
        return r;
    }

    public static FeedIterator CreateStreamFeedIterator(
        List<string> items,
        Func<string> sessionTokenProvider,
        int initialOffset = 0,
        int? maxItemCount = null)
        => CreateStreamFeedIteratorFromFactory(() => items, sessionTokenProvider, initialOffset, maxItemCount ?? items.Count);

    public static FeedIterator CreateStreamFeedIteratorFromFactory(
        Func<List<string>> itemsFactory,
        Func<string> sessionTokenProvider,
        int initialOffset,
        int? maxItemCount)
    {
        var offset = initialOffset;
        var done = false;

        var feedIterator = Substitute.For<FeedIterator>();
        feedIterator.HasMoreResults.Returns(_ => !done);
        feedIterator.ReadNextAsync(Arg.Any<CancellationToken>()).Returns(callInfo =>
        {
            var ct = callInfo.Arg<CancellationToken>();
            ct.ThrowIfCancellationRequested();
            var items = itemsFactory();
            var pageSize = maxItemCount ?? items.Count;
            if (pageSize <= 0) pageSize = items.Count;
            var page = items.Skip(offset).Take(pageSize).ToList();
            offset += page.Count;
            if (offset >= items.Count)
                done = true;
            var documentsArray = new JArray(page.Select(JsonParseHelpers.ParseJson));
            var envelope = new JObject
            {
                ["Documents"] = documentsArray,
                ["_count"] = documentsArray.Count,
                ["_rid"] = string.Empty
            };
            var stream = ToStream(envelope.ToString(Formatting.None));
            var response = new ResponseMessage(HttpStatusCode.OK) { Content = stream };
            response.Headers["x-ms-activity-id"] = Guid.NewGuid().ToString();
            response.Headers["x-ms-request-charge"] = "1";
            response.Headers["x-ms-session-token"] = sessionTokenProvider();
            response.Headers["x-ms-item-count"] = documentsArray.Count.ToString();
            if (!done)
                response.Headers.Add("x-ms-continuation", offset.ToString());
            return Task.FromResult(response);
        });
        return feedIterator;
    }

    private sealed class InMemoryDiagnostics : CosmosDiagnostics
    {
        public override TimeSpan GetClientElapsedTime() => TimeSpan.Zero;
        public override IReadOnlyList<(string regionName, Uri uri)> GetContactedRegions() => Array.Empty<(string, Uri)>();
        public override string ToString() => "{}";
    }

    private sealed class InMemoryItemResponse<T> : ItemResponse<T>
    {
        private readonly HttpStatusCode _statusCode;
        private readonly Headers _headers;
        private readonly T _resource;
        private readonly CosmosDiagnostics _diagnostics;
        private readonly double _requestCharge;
        private readonly string _activityId;
        private readonly string? _etag;

        public InMemoryItemResponse(
            HttpStatusCode statusCode,
            Headers headers,
            T resource,
            CosmosDiagnostics diagnostics,
            double requestCharge,
            string activityId,
            string? etag)
        {
            _statusCode = statusCode;
            _headers = headers;
            _resource = resource;
            _diagnostics = diagnostics;
            _requestCharge = requestCharge;
            _activityId = activityId;
            _etag = etag;
        }

        public override HttpStatusCode StatusCode => _statusCode;
        public override Headers Headers => _headers;
        public override T Resource => _resource;
        public override CosmosDiagnostics Diagnostics => _diagnostics;
        public override double RequestCharge => _requestCharge;
        public override string ActivityId => _activityId;
        public override string ETag => _etag!;
    }

    private sealed class InMemoryFeedResponse<T> : FeedResponse<T>
    {
        private readonly IReadOnlyList<T> _items;
        private readonly HttpStatusCode _statusCode;
        private readonly string? _etag;

        public InMemoryFeedResponse(
            IReadOnlyList<T> items,
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string? etag = null)
        {
            _items = items;
            _statusCode = statusCode;
            _etag = etag;
            Headers["x-ms-request-charge"] = SyntheticRequestCharge.ToString(CultureInfo.InvariantCulture);
            Headers["x-ms-item-count"] = items.Count.ToString(CultureInfo.InvariantCulture);
        }

        public override Headers Headers { get; } = new();
        public override IEnumerable<T> Resource => _items;
        public override HttpStatusCode StatusCode => _statusCode;
        public override CosmosDiagnostics Diagnostics => FakeDiagnostics;
        public override int Count => _items.Count;
        public override string IndexMetrics => null!;
        public override string ContinuationToken => null!;
        public override double RequestCharge => SyntheticRequestCharge;
        public override string ActivityId { get; } = Guid.NewGuid().ToString();
        public override string ETag => _etag!;
        public override IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    }
}
