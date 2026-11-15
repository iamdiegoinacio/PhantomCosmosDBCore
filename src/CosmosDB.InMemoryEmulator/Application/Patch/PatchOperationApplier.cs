#nullable enable
using System.Net;
using System.Text;
using CosmosDB.InMemoryEmulator.Infrastructure.SdkInterop;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json.Linq;
using static CosmosDB.InMemoryEmulator.Domain.Primitives.ContainerConstants;

namespace CosmosDB.InMemoryEmulator.Application.Patch;

/// <summary>
/// Applies Cosmos DB patch operations (Add, Set, Replace, Remove, Move, Increment)
/// to a <see cref="JObject"/> document.
/// </summary>
internal static class PatchOperationApplier
{
    private static readonly HashSet<string> SystemProperties = new(StringComparer.OrdinalIgnoreCase)
        { "/_ts", "/_etag", "/_rid", "/_self", "/_attachments" };

    public static void ApplyPatchOperations(JObject jObj, IReadOnlyList<PatchOperation> patchOperations)
    {
        foreach (var operation in patchOperations)
        {
            var path = operation.Path;

            // Reject patches to system-generated properties
            if (SystemProperties.Contains(path))
            {
                throw InMemoryCosmosException.Create(
                    $"Cannot patch system property '{path}'.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }

            var segments = path.TrimStart('/').Split('/');
            var propertyName = segments.Last();
            var parentPath = BuildSelectTokenPath(segments.Take(segments.Length - 1));
            var rawParent = segments.Length > 1 ? jObj.SelectToken(parentPath) : (JToken)jObj;

            switch (operation.OperationType)
            {
                case PatchOperationType.Add:
                {
                    var value = CosmosSdkIntrospector.GetPatchValue(operation);
                    var newToken = value is not null ? JToken.FromObject(value) : JValue.CreateNull();

                    if (propertyName == "-" && rawParent is JArray appendArray)
                    {
                        appendArray.Add(newToken);
                    }
                    else if (int.TryParse(propertyName, out var insertIdx) && rawParent is JArray insertArray)
                    {
                        if (insertIdx < 0 || insertIdx > insertArray.Count)
                        {
                            throw InMemoryCosmosException.Create("Array index out of bounds.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        insertArray.Insert(insertIdx, newToken);
                    }
                    else
                    {
                        var parent = rawParent as JObject ?? jObj;
                        parent[propertyName] = newToken;
                    }

                    break;
                }
                case PatchOperationType.Set:
                {
                    var value = CosmosSdkIntrospector.GetPatchValue(operation);
                    var newToken = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
                    if (int.TryParse(propertyName, out var idx) && rawParent is JArray arr)
                    {
                        if (idx < 0 || idx >= arr.Count)
                        {
                            throw InMemoryCosmosException.Create("Array index out of bounds.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        arr[idx] = newToken;
                    }
                    else
                    {
                        var parent = rawParent as JObject ?? jObj;
                        parent[propertyName] = newToken;
                    }
                    break;
                }
                case PatchOperationType.Replace:
                {
                    var value = CosmosSdkIntrospector.GetPatchValue(operation);
                    var newToken = value is not null ? JToken.FromObject(value) : JValue.CreateNull();
                    if (int.TryParse(propertyName, out var idx) && rawParent is JArray arr)
                    {
                        if (idx < 0 || idx >= arr.Count)
                        {
                            throw InMemoryCosmosException.Create("Array index out of bounds.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        arr[idx] = newToken;
                    }
                    else
                    {
                        var parent = rawParent as JObject ?? jObj;
                        if (parent[propertyName] is null)
                        {
                            throw InMemoryCosmosException.Create("Replace target does not exist.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        parent[propertyName] = newToken;
                    }
                    break;
                }
                case PatchOperationType.Remove:
                {
                    if (int.TryParse(propertyName, out var idx) && rawParent is JArray arr)
                    {
                        if (idx < 0 || idx >= arr.Count)
                        {
                            throw InMemoryCosmosException.Create("Array index out of bounds.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        arr.RemoveAt(idx);
                    }
                    else
                    {
                        var parent = rawParent as JObject ?? jObj;
                        if (parent[propertyName] is null)
                        {
                            throw InMemoryCosmosException.Create("Remove target does not exist.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        parent.Remove(propertyName);
                    }
                    break;
                }
                case PatchOperationType.Move:
                {
                    var sourcePath = CosmosSdkIntrospector.GetPatchSourcePath(operation);
                    if (sourcePath is not null)
                    {
                        // Reject when destination is a child of source (e.g. move /nested → /nested/child)
                        if (path.StartsWith(sourcePath + "/", StringComparison.Ordinal))
                        {
                            throw InMemoryCosmosException.Create(
                                "The 'path' attribute can't be a JSON child of the 'from' JSON location.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }

                        var sourceSegments = sourcePath.TrimStart('/').Split('/');
                        var sourcePropertyName = sourceSegments.Last();
                        var sourceParentPath = BuildSelectTokenPath(sourceSegments.Take(sourceSegments.Length - 1));
                        var sourceParent = sourceSegments.Length > 1
                            ? jObj.SelectToken(sourceParentPath) as JObject ?? jObj
                            : jObj;
                        var sourceValue = sourceParent[sourcePropertyName];
                        if (sourceValue is null)
                        {
                            throw InMemoryCosmosException.Create("Move source does not exist.",
                                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                        }
                        sourceParent.Remove(sourcePropertyName);
                        var parent = rawParent as JObject ?? jObj;
                        parent[propertyName] = sourceValue;
                    }

                    break;
                }
                case PatchOperationType.Increment:
                {
                    var incrementValue = CosmosSdkIntrospector.GetPatchValue(operation);
                    if (incrementValue is not null)
                    {
                        var parent = rawParent as JObject ?? jObj;
                        var existingToken = parent[propertyName];
                        if (existingToken is not null)
                        {
                            if (existingToken.Type is not (JTokenType.Integer or JTokenType.Float))
                            {
                                throw InMemoryCosmosException.Create(
                                    $"Cannot increment non-numeric field '{path}'. Field type is {existingToken.Type}.",
                                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
                            }
                            var existingDouble = existingToken.Value<double>();
                            var incrementDouble = Convert.ToDouble(incrementValue);
                            var result = existingDouble + incrementDouble;
                            if (existingToken.Type == JTokenType.Integer && result == Math.Floor(result))
                            {
                                parent[propertyName] = (long)result;
                            }
                            else
                            {
                                parent[propertyName] = result;
                            }
                        }
                        else
                        {
                            var incrementDouble = Convert.ToDouble(incrementValue);
                            if (incrementDouble == Math.Floor(incrementDouble))
                            {
                                parent[propertyName] = (long)incrementDouble;
                            }
                            else
                            {
                                parent[propertyName] = incrementDouble;
                            }
                        }
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Converts path segments to a Newtonsoft.Json SelectToken-compatible path.
    /// Numeric segments become array indexers (e.g., ["runs","0"] → "runs[0]").
    /// </summary>
    public static string BuildSelectTokenPath(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            if (int.TryParse(segment, out _))
            {
                sb.Append('[').Append(segment).Append(']');
            }
            else
            {
                if (sb.Length > 0) sb.Append('.');
                sb.Append(segment);
            }
        }
        return sb.ToString();
    }
}
