#nullable enable
using System.Globalization;
using System.Net;
using System.Text;
using Phantom.CosmosDB.Core.Domain.Primitives;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Phantom.CosmosDB.Core.Domain.Primitives.ContainerConstants;

namespace Phantom.CosmosDB.Core.Domain.Validation;

/// <summary>
/// Extracts, normalizes, converts, and validates single and composite (hierarchical)
/// partition keys for Cosmos DB documents.
/// </summary>
internal static class PartitionKeyProcessor
{
    public static string? ExtractPartitionKeyValue(
        PartitionKey? partitionKey, JObject jObj, IReadOnlyList<string> partitionKeyPaths)
    {
        var pk = ExtractPartitionKeyValueCore(partitionKey, jObj, partitionKeyPaths);
        if (pk is not null && Encoding.UTF8.GetByteCount(pk) > 2048)
        {
            throw InMemoryCosmosException.Create(
                "Partition key value exceeds the maximum allowed size of 2KB.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        return pk;
    }

    public static string? ExtractPartitionKeyValueCore(
        PartitionKey? partitionKey, JObject jObj, IReadOnlyList<string> partitionKeyPaths)
    {
        if (partitionKey.HasValue)
        {
            if (partitionKey.Value == PartitionKey.Null)
            {
                return null;
            }

            if (partitionKey.Value != PartitionKey.None)
            {
                return PartitionKeyToString(partitionKey.Value);
            }
        }

        if (partitionKeyPaths is { Count: > 0 })
        {
            if (partitionKeyPaths.Count > 1)
            {
                // Composite key — preserve null positions as empty strings for consistency
                // with PartitionKeyToString which also uses empty string for null components
                var parts = partitionKeyPaths.Select(path =>
                {
                    var t = jObj.SelectToken(path.TrimStart('/'));
                    return t is not null ? JTokenToTypedKey(t) : string.Empty;
                }).ToList();
                return string.Join("|", parts.Select(p => p ?? string.Empty));
            }

            // Single path — check if the field exists
            var token = jObj.SelectToken(partitionKeyPaths[0].TrimStart('/'));
            if (token is not null)
            {
                // Field exists — if value is null, return null (don't fall back to id)
                return token.Type == JTokenType.Null ? null : JTokenToTypedKey(token);
            }
        }

        return null;
    }

    public static string? JTokenToTypedKey(JToken token)
    {
        return token.Type switch
        {
            JTokenType.String => "S:" + token.Value<string>(),
            JTokenType.Integer or JTokenType.Float => "N:" + token.Value<double>().ToString("R", CultureInfo.InvariantCulture),
            JTokenType.Boolean => "B:" + (token.Value<bool>() ? "true" : "false"),
            JTokenType.Null => null,
            _ => token.ToString()
        };
    }

    /// <summary>
    /// Converts a type-prefixed internal key back to a JToken for document fields.
    /// </summary>
    public static JToken TypedKeyToJToken(string? typedKey)
    {
        if (typedKey is null) return JValue.CreateNull();
        if (typedKey.Length >= 2 && typedKey[1] == ':')
        {
            var value = typedKey.Substring(2);
            return typedKey[0] switch
            {
                'S' => new JValue(value),
                'N' => new JValue(double.Parse(value, CultureInfo.InvariantCulture)),
                'B' => new JValue(value == "true"),
                _ => new JValue(typedKey)
            };
        }
        return new JValue(typedKey);
    }

    /// <summary>
    /// Converts a type-prefixed internal key back to a typed PartitionKey.
    /// </summary>
    public static PartitionKey TypedKeyToPartitionKey(string? typedKey)
    {
        if (typedKey is null) return PartitionKey.Null;
        if (typedKey.Length >= 2 && typedKey[1] == ':')
        {
            var value = typedKey.Substring(2);
            return typedKey[0] switch
            {
                'S' => new PartitionKey(value),
                'N' => new PartitionKey(double.Parse(value, CultureInfo.InvariantCulture)),
                'B' => new PartitionKey(bool.Parse(value)),
                _ => new PartitionKey(typedKey)
            };
        }
        return new PartitionKey(typedKey);
    }

    public static string? PartitionKeyToString(PartitionKey partitionKey)
    {
        if (partitionKey == PartitionKey.None || partitionKey == PartitionKey.Null)
        {
            return null;
        }

        var raw = partitionKey.ToString();
        if (raw.StartsWith("["))
        {
            try
            {
                var arr = JArray.Parse(raw);
                if (arr.Count == 1)
                {
                    return JTokenToTypedKey(arr[0]);
                }

                return string.Join("|", arr.Select(JTokenToTypedKey));
            }
            catch { /* fall through */ }
        }
        return raw;
    }

    public static int CountPartitionKeyComponents(PartitionKey partitionKey)
    {
        try
        {
            var raw = partitionKey.ToString();
            if (raw.StartsWith("["))
            {
                var arr = JArray.Parse(raw);
                return arr.Count;
            }
            return 1;
        }
        catch { return 1; }
    }

    public static string ExtractPartitionKeyValueFromJson(
        string json, IReadOnlyList<string> partitionKeyPaths, JsonSerializerSettings jsonSettings)
    {
        JToken? token;
        try { token = JsonConvert.DeserializeObject<JToken>(json, jsonSettings); }
        catch { return ""; }

        if (token is not JObject jObj) return "";

        if (partitionKeyPaths is { Count: > 0 })
        {
            var parts = partitionKeyPaths.Select(path =>
            {
                var t = jObj.SelectToken(path.TrimStart('/'));
                return t is not null ? JTokenToTypedKey(t) : null;
            }).ToList();
            if (parts.Count == 1) return parts[0] ?? "";
            return string.Join("|", parts.Select(p => p ?? ""));
        }

        return "";
    }

    public static void ValidatePartitionKeyPaths(IReadOnlyList<string>? partitionKeyPaths)
    {
        if (partitionKeyPaths is null || partitionKeyPaths.Count == 0)
        {
            throw InMemoryCosmosException.Create(
                "At least one partition key path must be specified.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        if (partitionKeyPaths.Count > 3)
        {
            throw InMemoryCosmosException.Create(
                "A container can define up to 3 hierarchical partition key paths.",
                HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
        }

        foreach (var path in partitionKeyPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/'))
            {
                throw InMemoryCosmosException.Create(
                    $"Partition key path '{path}' is invalid. Partition key path must start with '/'.",
                    HttpStatusCode.BadRequest, 0, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
        }
    }

    public static void ValidatePartitionKeyConsistency(
        PartitionKey? explicitKey, JObject jObj, IReadOnlyList<string> partitionKeyPaths)
    {
        if (!explicitKey.HasValue || explicitKey.Value == PartitionKey.None || explicitKey.Value == PartitionKey.Null)
            return;

        if (partitionKeyPaths is null || partitionKeyPaths.Count == 0)
            return;

        if (partitionKeyPaths.Count == 1)
        {
            var pkPath = partitionKeyPaths[0].TrimStart('/');
            var bodyToken = jObj.SelectToken(pkPath);
            if (bodyToken is null)
                return; // Field not present in body — nothing to validate

            var explicitPk = PartitionKeyToString(explicitKey.Value);
            var bodyPk = JTokenToTypedKey(bodyToken);

            if (bodyPk != explicitPk)
            {
                throw InMemoryCosmosException.Create(
                    "PartitionKey extracted from document doesn't match the one specified in the header. " +
                    "Learn more: https://aka.ms/CosmosDB/sql/errors/wrong-pk-value",
                    HttpStatusCode.BadRequest, CosmosSubStatusCodes.PartitionKeyMismatch, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
        }
        else
        {
            var anyTokenPresent = partitionKeyPaths.Any(p => jObj.SelectToken(p.TrimStart('/')) is not null);
            if (!anyTokenPresent)
                return;

            var explicitPk = PartitionKeyToString(explicitKey.Value);
            var bodyPk = ExtractPartitionKeyValueCore(null, jObj, partitionKeyPaths);

            if (bodyPk is not null && explicitPk is not null && bodyPk != explicitPk)
            {
                throw InMemoryCosmosException.Create(
                    "PartitionKey extracted from document doesn't match the one specified in the header. " +
                    "Learn more: https://aka.ms/CosmosDB/sql/errors/wrong-pk-value",
                    HttpStatusCode.BadRequest, CosmosSubStatusCodes.PartitionKeyMismatch, Guid.NewGuid().ToString(), SyntheticRequestCharge);
            }
        }
    }

    public static bool IsPartitionKeyConsistent(
        PartitionKey? explicitKey, JObject jObj, IReadOnlyList<string> partitionKeyPaths)
    {
        if (!explicitKey.HasValue || explicitKey.Value == PartitionKey.None || explicitKey.Value == PartitionKey.Null)
            return true;

        if (partitionKeyPaths is null || partitionKeyPaths.Count == 0)
            return true;

        if (partitionKeyPaths.Count == 1)
        {
            var pkPath = partitionKeyPaths[0].TrimStart('/');
            var bodyToken = jObj.SelectToken(pkPath);
            if (bodyToken is null)
                return true;

            var explicitPk = PartitionKeyToString(explicitKey.Value);
            var bodyPk = JTokenToTypedKey(bodyToken);

            return bodyPk == explicitPk;
        }
        else
        {
            var anyTokenPresent = partitionKeyPaths.Any(p => jObj.SelectToken(p.TrimStart('/')) is not null);
            if (!anyTokenPresent)
                return true;

            var explicitPk = PartitionKeyToString(explicitKey.Value);
            var bodyPk = ExtractPartitionKeyValueCore(null, jObj, partitionKeyPaths);

            return bodyPk == explicitPk;
        }
    }
}
