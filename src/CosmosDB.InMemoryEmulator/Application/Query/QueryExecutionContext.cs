#nullable enable
using Microsoft.Azure.Cosmos.Scripts;

namespace CosmosDB.InMemoryEmulator.Application.Query;

/// <summary>
/// Encapsulates query-scoped state including user parameters, snapshot timestamps for
/// <c>GetCurrentDateTime*</c>/<c>GetCurrentTimestamp*</c>/<c>GetCurrentTicks*</c>, and UDF execution bindings.
/// Replaces the legacy magic-key dictionary injection pattern.
/// </summary>
internal sealed class QueryExecutionContext
{
    public IDictionary<string, object?> Parameters { get; }
    public string StaticDateTime { get; }
    public long StaticTicks { get; }
    public long StaticTimestamp { get; }
    public IReadOnlyDictionary<string, Func<object?[], object?>>? UdfRegistry { get; }
    public IReadOnlyDictionary<string, UserDefinedFunctionProperties>? UdfProperties { get; }
    public IJsUdfEngine? JsUdfEngine { get; }
    public Func<object?[], object?>? UdfPlaceholder { get; }

    public QueryExecutionContext(
        IDictionary<string, object?>? parameters = null,
        IReadOnlyDictionary<string, Func<object?[], object?>>? udfRegistry = null,
        IReadOnlyDictionary<string, UserDefinedFunctionProperties>? udfProperties = null,
        IJsUdfEngine? jsUdfEngine = null,
        Func<object?[], object?>? udfPlaceholder = null,
        DateTime? nowUtc = null)
    {
        Parameters = parameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        UdfRegistry = udfRegistry;
        UdfProperties = udfProperties;
        JsUdfEngine = jsUdfEngine;
        UdfPlaceholder = udfPlaceholder;

        var now = nowUtc ?? DateTime.UtcNow;
        StaticDateTime = now.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        StaticTicks = now.Ticks;
        StaticTimestamp = new DateTimeOffset(now).ToUnixTimeMilliseconds();
    }

    public object? InvokeUdf(string fullFunctionName, object?[] args)
    {
        var udfName = fullFunctionName.Substring(4); // strip "UDF." prefix

        if (UdfRegistry is not null &&
            UdfRegistry.TryGetValue(fullFunctionName, out var udfImpl) &&
            !ReferenceEquals(udfImpl, UdfPlaceholder))
        {
            return udfImpl(args);
        }

        if (UdfProperties is not null &&
            UdfProperties.TryGetValue(udfName, out var udfProp) &&
            udfProp.Body is not null &&
            JsUdfEngine is not null)
        {
            return JsUdfEngine.ExecuteUdf(udfProp.Body, args!);
        }

        throw new NotSupportedException(
            $"Unregistered user-defined function: {fullFunctionName}. " +
            "Call RegisterUdf() on the InMemoryContainer to register it before querying.");
    }
}
