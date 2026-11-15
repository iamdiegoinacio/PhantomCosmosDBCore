#nullable enable
using CosmosDB.InMemoryEmulator.Domain.Primitives;
using Newtonsoft.Json.Linq;

namespace CosmosDB.InMemoryEmulator.Application.Query.Functions;

/// <summary>
/// Central dispatcher for all built-in and user-defined Cosmos SQL functions.
/// Enforces Cosmos DB undefined-propagation semantics and delegates to specialized function handlers.
/// </summary>
internal static class SqlFunctionEvaluator
{
    public static object? Evaluate(
        FunctionCallExpression func,
        JObject item,
        string fromAlias,
        QueryExecutionContext context,
        Func<SqlExpression, object?> evaluateExpression,
        Func<CosmosSqlQuery, object?> evaluateArraySubquery)
    {
        if (string.Equals(func.FunctionName, "ARRAY", StringComparison.OrdinalIgnoreCase) &&
            func.Arguments.Length == 1 && func.Arguments[0] is SubqueryExpression subExpr)
        {
            return evaluateArraySubquery(subExpr.Subquery);
        }

        var args = func.Arguments.Select(evaluateExpression).ToArray();

        if (args.Any(static a => a is UndefinedValue))
        {
            var name = func.FunctionName.ToUpperInvariant();
            if (!name.StartsWith("IS_", StringComparison.Ordinal) &&
                name is not "COALESCE" and not "IIF"
                and not "ARRAY_CONTAINS" and not "ARRAY_CONTAINS_ANY" and not "ARRAY_CONTAINS_ALL"
                and not "DOCUMENTID" and not "VECTORDISTANCE"
                and not "FULLTEXTSCORE" and not "FULLTEXTCONTAINS" and not "FULLTEXTCONTAINSALL" and not "FULLTEXTCONTAINSANY"
                and not "INDEX_OF" and not "TYPE")
            {
                return UndefinedValue.Instance;
            }
        }

        if (TypeCheckFunctions.TryEvaluate(func, args, item, fromAlias, context.Parameters, out var typeCheckResult))
        {
            return typeCheckResult;
        }

        if (StringFunctions.TryEvaluate(func.FunctionName, args, out var stringResult))
        {
            return stringResult;
        }

        if (ArrayObjectFunctions.TryEvaluate(func, args, item, fromAlias, evaluateExpression, out var arrayResult))
        {
            return arrayResult;
        }

        if (MathFunctions.TryEvaluate(func.FunctionName, args, out var mathResult))
        {
            return mathResult;
        }

        if (DateTimeFunctions.TryEvaluate(func.FunctionName, args, context, out var dateTimeResult))
        {
            return dateTimeResult;
        }

        if (FullTextSearchFunctions.TryEvaluate(func.FunctionName, args, out var fullTextResult))
        {
            return fullTextResult;
        }

        switch (func.FunctionName)
        {
            case "COUNT":
                return 1L;
            case "SUM":
            case "AVG":
            case "MIN":
            case "MAX":
                return args.Length > 0 ? args[0] : null;

            case "IIF":
                if (args.Length < 3) return null;
                return args[0] is bool b && b ? args[1] : args[2];

            case "DOCUMENTID":
                return item["_rid"]?.Value<string>() ?? item["id"]?.Value<string>();

            case "ST_DISTANCE":
                return args.Length >= 2 ? SpatialGeoFunctions.StDistance(args[0], args[1]) : null;
            case "ST_WITHIN":
                return args.Length >= 2 ? (object)SpatialGeoFunctions.StWithin(args[0], args[1]) : null;
            case "ST_INTERSECTS":
                return args.Length >= 2 ? (object)SpatialGeoFunctions.StIntersects(args[0], args[1]) : null;
            case "ST_ISVALID":
                return args.Length >= 1 ? (object)SpatialGeoFunctions.StIsValid(args[0]) : false;
            case "ST_ISVALIDDETAILED":
                return args.Length >= 1 ? SpatialGeoFunctions.StIsValidDetailed(args[0]) : null;
            case "ST_AREA":
                return args.Length >= 1 ? SpatialGeoFunctions.StArea(args[0]) : null;

            case "VECTORDISTANCE":
                return args.Length >= 2 ? VectorFunctions.EvaluateVectorDistance(args) : null;

            case "COALESCE":
                foreach (var arg in args)
                {
                    if (arg is UndefinedValue) continue;
                    return arg;
                }
                return UndefinedValue.Instance;

            default:
                if (func.FunctionName.StartsWith("UDF.", StringComparison.OrdinalIgnoreCase))
                {
                    return context.InvokeUdf(func.FunctionName, args);
                }

                throw new NotSupportedException($"Unsupported Cosmos SQL function: {func.FunctionName}");
        }
    }
}
