#nullable enable
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Azure.Cosmos;

namespace CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;

/// <summary>
/// Centralizes and caches reflection access to non-public members of the Azure Cosmos DB SDK
/// (<see cref="ChangeFeedStartFrom"/>, <see cref="QueryDefinition"/>, <see cref="PatchOperation"/>).
/// </summary>
internal static class CosmosSdkIntrospector
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> PatchFromPropertyCache = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> PatchValuePropertyCache = new();
    private static readonly FieldInfo? QueryParametersField = typeof(QueryDefinition)
        .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
        .FirstOrDefault(f => f.Name.Contains("parameter", StringComparison.OrdinalIgnoreCase));

    public static DateTime? ExtractStartTime(ChangeFeedStartFrom startFrom)
    {
        var type = startFrom.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(DateTime) && prop.GetValue(startFrom) is DateTime dt)
            {
                return dt;
            }
        }

        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (field.FieldType == typeof(DateTime) && field.GetValue(startFrom) is DateTime dt)
            {
                return dt;
            }
        }

        return null;
    }

    public static FeedRange? ExtractFeedRangeFromStartFrom(ChangeFeedStartFrom startFrom)
    {
        var type = startFrom.GetType();
        foreach (var prop in type.GetProperties(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(FeedRange).IsAssignableFrom(prop.PropertyType) && prop.GetValue(startFrom) is FeedRange fr)
                return fr;
        }

        foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (typeof(FeedRange).IsAssignableFrom(field.FieldType) && field.GetValue(startFrom) is FeedRange fr)
                return fr;
        }

        return null;
    }

    public static Dictionary<string, object?> ExtractQueryParameters(QueryDefinition? queryDefinition)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (queryDefinition is null)
        {
            return parameters;
        }

        try
        {
            foreach (var (name, value) in queryDefinition.GetQueryParameters())
            {
                parameters[name] = value;
            }

            if (parameters.Count > 0)
            {
                return parameters;
            }
        }
        catch
        {
            // Fall through to reflection
        }

        try
        {
            if (QueryParametersField?.GetValue(queryDefinition) is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is null) continue;
                    var itemType = item.GetType();
                    var nameProp = itemType.GetProperty("Name") ?? itemType.GetProperty("Item1");
                    var valueProp = itemType.GetProperty("Value") ?? itemType.GetProperty("Item2");
                    if (nameProp is not null && valueProp is not null)
                    {
                        var name = nameProp.GetValue(item)?.ToString();
                        if (name is not null)
                        {
                            parameters[name] = valueProp.GetValue(item);
                        }
                    }
                    else
                    {
                        var nameField = itemType.GetField("Name") ?? itemType.GetField("Item1");
                        var valueField = itemType.GetField("Value") ?? itemType.GetField("Item2");
                        if (nameField is not null && valueField is not null)
                        {
                            var name = nameField.GetValue(item)?.ToString();
                            if (name is not null)
                            {
                                parameters[name] = valueField.GetValue(item);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Parameters cannot be extracted
        }

        return parameters;
    }

    public static string? GetPatchSourcePath(PatchOperation operation)
    {
        var prop = PatchFromPropertyCache.GetOrAdd(
            operation.GetType(),
            static t => t.GetProperty("From", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        return prop?.GetValue(operation)?.ToString();
    }

    public static object? GetPatchValue(PatchOperation operation)
    {
        var prop = PatchValuePropertyCache.GetOrAdd(
            operation.GetType(),
            static t => t.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance));
        return prop?.GetValue(operation);
    }
}
