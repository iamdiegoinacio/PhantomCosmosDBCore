#nullable enable
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query.Functions;

/// <summary>
/// Evaluates approximate full-text search Cosmos SQL functions (<c>FULLTEXTCONTAINS</c>,
/// <c>FULLTEXTCONTAINSALL</c>, <c>FULLTEXTCONTAINSANY</c>, and <c>FULLTEXTSCORE</c>).
/// </summary>
internal static class FullTextSearchFunctions
{
    public static bool TryEvaluate(string functionName, object?[] args, out object? result)
    {
        switch (functionName)
        {
            case "FULLTEXTCONTAINS":
                if (args.Length < 2) { result = false; return true; }
                var text = args[0]?.ToString();
                var term = args[1]?.ToString();
                result = text is not null && term is not null && text.Contains(term, StringComparison.OrdinalIgnoreCase);
                return true;

            case "FULLTEXTCONTAINSALL":
                if (args.Length < 2) { result = false; return true; }
                var allText = args[0]?.ToString();
                if (allText is null) { result = false; return true; }
                for (var i = 1; i < args.Length; i++)
                {
                    var t = args[i]?.ToString();
                    if (t is null || !allText.Contains(t, StringComparison.OrdinalIgnoreCase))
                    {
                        result = false;
                        return true;
                    }
                }
                result = true;
                return true;

            case "FULLTEXTCONTAINSANY":
                if (args.Length < 2) { result = false; return true; }
                var anyText = args[0]?.ToString();
                if (anyText is null) { result = false; return true; }
                for (var i = 1; i < args.Length; i++)
                {
                    var t = args[i]?.ToString();
                    if (t is not null && anyText.Contains(t, StringComparison.OrdinalIgnoreCase))
                    {
                        result = true;
                        return true;
                    }
                }
                result = false;
                return true;

            case "FULLTEXTSCORE":
                if (args.Length < 2) { result = 0.0; return true; }
                var scoreText = args[0]?.ToString();
                if (scoreText is null) { result = 0.0; return true; }
                var score = 0.0;
                if (args[1] is JArray searchTerms)
                {
                    foreach (var termToken in searchTerms)
                    {
                        var st = termToken.Value<string>();
                        if (st is not null)
                        {
                            score += SqlMathHelpers.CountOccurrences(scoreText, st);
                        }
                    }
                }
                else
                {
                    for (var i = 1; i < args.Length; i++)
                    {
                        var st = args[i]?.ToString();
                        if (st is not null)
                        {
                            score += SqlMathHelpers.CountOccurrences(scoreText, st);
                        }
                    }
                }
                result = score;
                return true;

            default:
                result = null;
                return false;
        }
    }
}
