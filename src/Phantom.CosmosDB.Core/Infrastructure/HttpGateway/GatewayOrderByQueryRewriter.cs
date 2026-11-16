#nullable enable
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Infrastructure.HttpGateway;

/// <summary>
/// Rewrites and projects SQL queries and result payloads to match the internal
/// wire format expected by the Cosmos SDK's <c>OrderBy</c> and <c>GroupBy</c> pipeline stages.
/// </summary>
internal static class GatewayOrderByQueryRewriter
{
    public static string StripOffsetLimit(string sql)
    {
        return Regex.Replace(sql, @"\s*OFFSET\s+\S+\s+LIMIT\s+\S+", "", RegexOptions.IgnoreCase).TrimEnd();
    }

    public static string BuildOrderByRewrittenQuery(CosmosSqlQuery parsed)
    {
        var alias = parsed.FromAlias ?? "c";

        var orderByItemsParts = parsed.OrderByFields!
            .Select(field => $"{{\"item\": {field.Field ?? CosmosSqlParser.ExprToString(field.Expression)}}}")
            .ToList();
        var orderByItemsArray = $"[{string.Join(", ", orderByItemsParts)}]";

        var sb = new StringBuilder($"SELECT {alias}._rid, ");
        sb.Append(orderByItemsArray);
        sb.Append(" AS orderByItems, ");

        var hasComputedSelectField = parsed.SelectFields
            .Any(f => f.SqlExpr is FunctionCallExpression);
        if ((parsed.IsDistinct || hasComputedSelectField)
            && parsed.SelectFields.Length > 0
            && parsed.SelectFields.All(f => f.SqlExpr is not null))
        {
            var payloadParts = parsed.SelectFields.Select(f =>
            {
                var expr = CosmosSqlParser.ExprToString(f.SqlExpr);
                var key = f.Alias ?? f.Expression ?? expr;
                if (key.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase))
                    key = key[(alias.Length + 1)..];
                return $"\"{key}\": {expr}";
            });
            sb.Append($"{{{string.Join(", ", payloadParts)}}} AS payload");
        }
        else
        {
            sb.Append($"{alias} AS payload");
        }

        sb.Append($" FROM {alias}");

        if (parsed.WhereExpr is not null)
        {
            sb.Append(" WHERE ");
            sb.Append(CosmosSqlParser.ExprToString(parsed.WhereExpr));
        }

        var orderByStr = string.Join(", ", parsed.OrderByFields!.Select(field =>
            $"{field.Field ?? CosmosSqlParser.ExprToString(field.Expression)} {(field.Ascending ? "ASC" : "DESC")}"));
        sb.Append($" ORDER BY {orderByStr}");

        return sb.ToString();
    }

    public static string BuildComplexOrderBySql(
        CosmosSqlQuery parsed,
        string payloadAlias,
        out List<string> orderByAliases)
    {
        _ = payloadAlias;
        var alias = parsed.FromAlias ?? "c";
        var sb = new StringBuilder($"SELECT {alias} AS _doc");

        orderByAliases = [];
        for (var i = 0; i < parsed.OrderByFields!.Length; i++)
        {
            var field = parsed.OrderByFields[i];
            var obAlias = $"_ob{i}";
            orderByAliases.Add(obAlias);
            var expr = field.Field ?? CosmosSqlParser.ExprToString(field.Expression);
            sb.Append($", {expr} AS {obAlias}");
        }

        sb.Append($" FROM {alias}");

        if (parsed.WhereExpr is not null)
        {
            var simplifiedWhere = CosmosSqlParser.SimplifySdkWhereExpression(parsed.WhereExpr, alias);
            if (simplifiedWhere is not null)
                sb.Append($" WHERE {CosmosSqlParser.ExprToString(simplifiedWhere)}");
        }

        var orderByStr = string.Join(", ", parsed.OrderByFields.Select(field =>
            $"{field.Field ?? CosmosSqlParser.ExprToString(field.Expression)} {(field.Ascending ? "ASC" : "DESC")}"));
        sb.Append($" ORDER BY {orderByStr}");

        return sb.ToString();
    }

    public static string BuildFallbackOrderBySql(CosmosSqlQuery parsed)
    {
        var sb = new StringBuilder("SELECT ");

        if (parsed.IsDistinct)
        {
            sb.Append("DISTINCT ");
        }

        if (parsed.TopCount.HasValue)
        {
            sb.Append($"TOP {parsed.TopCount.Value} ");
        }

        sb.Append($"VALUE {parsed.FromAlias} FROM {parsed.FromAlias}");

        if (parsed.OrderByFields is { Length: > 0 })
        {
            var orderByStr = string.Join(", ", parsed.OrderByFields.Select(field =>
                $"{field.Field ?? CosmosSqlParser.ExprToString(field.Expression)} {(field.Ascending ? "ASC" : "DESC")}"));
            sb.Append($" ORDER BY {orderByStr}");
        }

        return sb.ToString();
    }

    public static List<string> ExtractOrderByItemPaths(CosmosSqlQuery parsed)
    {
        var orderByItemsField = parsed.SelectFields
            .FirstOrDefault(field => IsOrderByItemsArray(field.SqlExpr));

        if (orderByItemsField?.SqlExpr is ArrayLiteralExpression arrayExpr)
        {
            var paths = new List<string>();
            foreach (var element in arrayExpr.Elements)
            {
                if (element is ObjectLiteralExpression obj)
                {
                    var itemProp = obj.Properties.FirstOrDefault(property =>
                        string.Equals(property.Key, "item", StringComparison.OrdinalIgnoreCase));
                    if (itemProp.Value is IdentifierExpression ident)
                    {
                        paths.Add(StripFromAlias(ident.Name, parsed.FromAlias));
                    }
                    else if (itemProp.Value is not null)
                    {
                        var exprStr = CosmosSqlParser.ExprToString(itemProp.Value);
                        paths.Add(StripFromAlias(exprStr, parsed.FromAlias));
                    }
                }
            }

            if (paths.Count > 0)
            {
                return paths;
            }
        }

        if (parsed.OrderByFields is { Length: > 0 })
        {
            return parsed.OrderByFields
                .Select(field => StripFromAlias(
                    field.Field ?? CosmosSqlParser.ExprToString(field.Expression),
                    parsed.FromAlias))
                .ToList();
        }

        return [];
    }

    public static string StripFromAlias(string path, string fromAlias)
    {
        if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
        {
            return path[(fromAlias.Length + 1)..];
        }

        return path;
    }

    public static bool IsOrderByItemsArray(SqlExpression? expr)
    {
        return expr is ArrayLiteralExpression { Elements.Length: > 0 } arrayLiteral
            && arrayLiteral.Elements.All(element =>
                element is ObjectLiteralExpression obj
                && obj.Properties.Any(prop =>
                    string.Equals(prop.Key, "item", StringComparison.OrdinalIgnoreCase)));
    }

    public static JToken BuildPayloadValue(JObject doc, SelectField? payloadField, string fromAlias)
    {
        if (payloadField?.SqlExpr is ObjectLiteralExpression objLiteral)
        {
            var projected = new JObject();
            foreach (var prop in objLiteral.Properties)
            {
                var exprStr = CosmosSqlParser.ExprToString(prop.Value);
                var path = exprStr;
                if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    path = path[(fromAlias.Length + 1)..];
                projected[prop.Key] = doc.SelectToken(path)?.DeepClone() ?? JValue.CreateNull();
            }
            return projected;
        }

        if (payloadField?.SqlExpr is IdentifierExpression id
            && !string.Equals(id.Name, fromAlias, StringComparison.OrdinalIgnoreCase)
            && !id.Name.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase)
            && !id.Name.StartsWith(fromAlias + "[", StringComparison.OrdinalIgnoreCase))
        {
            var path = id.Name;
            if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                path = path[(fromAlias.Length + 1)..];
            return doc.SelectToken(path)?.DeepClone() ?? JValue.CreateNull();
        }

        return doc.DeepClone();
    }

    public static JToken BuildComplexPayloadValue(
        JObject fullDoc,
        ObjectLiteralExpression objLiteral,
        string fromAlias,
        CosmosSqlQuery parsed,
        JObject rawDoc,
        List<string> orderByAliases)
    {
        var projected = new JObject();
        foreach (var prop in objLiteral.Properties)
        {
            var exprStr = CosmosSqlParser.ExprToString(prop.Value);

            JToken? resolved = null;
            if (parsed.OrderByFields is not null)
            {
                for (var i = 0; i < parsed.OrderByFields.Length && i < orderByAliases.Count; i++)
                {
                    var obExpr = parsed.OrderByFields[i].Field
                        ?? CosmosSqlParser.ExprToString(parsed.OrderByFields[i].Expression);
                    if (string.Equals(exprStr, obExpr, StringComparison.OrdinalIgnoreCase))
                    {
                        resolved = rawDoc[orderByAliases[i]]?.DeepClone();
                        break;
                    }
                }
            }

            if (resolved is not null)
            {
                projected[prop.Key] = resolved;
            }
            else
            {
                var path = exprStr;
                if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    path = path[(fromAlias.Length + 1)..];
                projected[prop.Key] = fullDoc.SelectToken(path)?.DeepClone() ?? JValue.CreateNull();
            }
        }
        return projected;
    }

    public static string ReconstructGroupByQuery(CosmosSqlQuery parsed)
    {
        var alias = parsed.FromAlias ?? "c";
        var payloadField = parsed.SelectFields
            .FirstOrDefault(f => string.Equals(f.Alias, "payload", StringComparison.OrdinalIgnoreCase));

        if (payloadField?.SqlExpr is not ObjectLiteralExpression payloadObj)
            throw new InvalidOperationException("Cannot reconstruct GROUP BY query: payload field not found.");

        var selectParts = new List<string>();
        foreach (var prop in payloadObj.Properties)
        {
            if (prop.Value is ObjectLiteralExpression inner
                && inner.Properties.Length == 1
                && string.Equals(inner.Properties[0].Key, "item", StringComparison.OrdinalIgnoreCase))
            {
                selectParts.Add($"{CosmosSqlParser.ExprToString(inner.Properties[0].Value)} AS {prop.Key}");
            }
            else
            {
                selectParts.Add($"{CosmosSqlParser.ExprToString(prop.Value)} AS {prop.Key}");
            }
        }

        var sb = new StringBuilder($"SELECT {string.Join(", ", selectParts)} FROM {alias}");

        if (parsed.WhereExpr is not null)
            sb.Append($" WHERE {CosmosSqlParser.ExprToString(parsed.WhereExpr)}");

        sb.Append($" GROUP BY {string.Join(", ", parsed.GroupByFields!)}");

        return sb.ToString();
    }
}
