#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos SQL type-checking functions (<c>IS_DEFINED</c>, <c>IS_NULL</c>, <c>IS_ARRAY</c>,
/// <c>IS_BOOL</c>, <c>IS_NUMBER</c>, <c>IS_STRING</c>, <c>IS_OBJECT</c>, <c>IS_PRIMITIVE</c>,
/// <c>IS_FINITE_NUMBER</c>, <c>IS_INTEGER</c>, <c>IS_NAN</c>, and <c>TYPE</c>).
/// </summary>
internal static class TypeCheckFunctions
{
    public static bool TryEvaluate(
        FunctionCallExpression func,
        object?[] args,
        JObject item,
        string fromAlias,
        IDictionary<string, object?> parameters,
        out object? result)
    {
        switch (func.FunctionName)
        {
            case "IS_DEFINED":
                if (func.Arguments.Length < 1)
                {
                    result = false;
                    return true;
                }

                if (func.Arguments[0] is IdentifierExpression ident)
                {
                    var path = ident.Name;
                    if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path[(fromAlias.Length + 1)..];
                    }

                    result = item.SelectToken(path) != null;
                    return true;
                }

                if (func.Arguments[0] is ParameterExpression param)
                {
                    result = parameters.ContainsKey(param.Name);
                    return true;
                }

                result = args[0] != null;
                return true;

            case "IS_NULL":
                result = args.Length > 0 && args[0] is null;
                return true;

            case "IS_ARRAY":
                result = args.Length > 0 && (ResolveTokenType(func.Arguments, item, fromAlias) is JArray || args[0] is JArray);
                return true;

            case "IS_BOOL":
                result = args.Length > 0 && args[0] is bool;
                return true;

            case "IS_NUMBER":
                result = args.Length > 0 && args[0] is long or double or int or float or decimal;
                return true;

            case "IS_STRING":
                result = args.Length > 0 && args[0] is string;
                return true;

            case "IS_OBJECT":
                result = args.Length > 0 && (ResolveTokenType(func.Arguments, item, fromAlias) is JObject || args[0] is JObject);
                return true;

            case "IS_PRIMITIVE":
                if (args.Length == 0)
                {
                    result = false;
                    return true;
                }

                if (args[0] is null)
                {
                    result = true;
                    return true;
                }

                var tokenForPrimitive = ResolveTokenType(func.Arguments, item, fromAlias);
                if (tokenForPrimitive is JArray or JObject)
                {
                    result = false;
                    return true;
                }

                result = args[0] is string or bool or long or double;
                return true;

            case "IS_FINITE_NUMBER":
                if (args.Length == 0)
                {
                    result = false;
                    return true;
                }

                result = args[0] switch
                {
                    long => true,
                    int => true,
                    double d => !double.IsInfinity(d) && !double.IsNaN(d),
                    float f => !float.IsInfinity(f) && !float.IsNaN(f),
                    decimal => true,
                    _ => false,
                };
                return true;

            case "IS_INTEGER":
                result = args.Length > 0 && args[0] is long or int;
                return true;

            case "IS_NAN":
                result = args.Length > 0 && args[0] is double dNan && double.IsNaN(dNan);
                return true;

            case "TYPE":
                if (args.Length == 0 || args[0] is UndefinedValue)
                {
                    result = UndefinedValue.Instance;
                    return true;
                }

                var tokenForType = ResolveTokenType(func.Arguments, item, fromAlias);
                if (tokenForType is JArray)
                {
                    result = "array";
                    return true;
                }

                if (tokenForType is JObject)
                {
                    result = "object";
                    return true;
                }

                result = args[0] switch
                {
                    null => "null",
                    bool => "boolean",
                    long or int or double or float or decimal => "number",
                    string => "string",
                    _ => "undefined"
                };
                return true;

            default:
                result = null;
                return false;
        }
    }

    public static JToken? ResolveTokenType(SqlExpression[] arguments, JObject item, string fromAlias)
    {
        if (arguments.Length < 1)
        {
            return null;
        }

        if (arguments[0] is IdentifierExpression ident)
        {
            var path = ident.Name;
            if (path.StartsWith(fromAlias + ".", StringComparison.OrdinalIgnoreCase))
            {
                path = path[(fromAlias.Length + 1)..];
            }

            return item.SelectToken(path);
        }

        return null;
    }
}
