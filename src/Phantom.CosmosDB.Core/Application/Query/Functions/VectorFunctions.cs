#nullable enable
using System.Net;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.Core.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos DB vector similarity and distance functions (<c>VECTORDISTANCE</c>:
/// cosine similarity, dot product, and Euclidean distance).
/// </summary>
internal static class VectorFunctions
{
    public static object? EvaluateVectorDistance(object?[] args)
    {
        if (args.Length > 4)
        {
            throw InMemoryCosmosException.Create(
                "VECTORDISTANCE accepts at most 4 arguments.",
                HttpStatusCode.BadRequest, 0, string.Empty, 0);
        }

        var vec1 = ToDoubleArray(args[0]);
        var vec2 = ToDoubleArray(args[1]);
        if (vec1 is null || vec2 is null || vec1.Length != vec2.Length || vec1.Length == 0)
        {
            return null;
        }

        var distanceFunction = "cosine";
        if (args.Length > 3)
        {
            var options = args[3] switch
            {
                JObject jo => jo,
                string s when s.TrimStart().StartsWith("{", StringComparison.Ordinal) => JObject.Parse(s),
                string s => new JObject { ["distanceFunction"] = s },
                _ => null,
            };
            var df = options?["distanceFunction"]?.ToString();
            if (df is not null)
            {
                distanceFunction = df;
            }
        }

        var result = distanceFunction.ToLowerInvariant() switch
        {
            "cosine" => CosineSimilarity(vec1, vec2),
            "dotproduct" => (object)DotProduct(vec1, vec2),
            "euclidean" => (object)EuclideanDistance(vec1, vec2),
            _ => throw InMemoryCosmosException.Create(
                $"Unknown distanceFunction '{distanceFunction}'. Supported values: 'cosine', 'dotproduct', 'euclidean'.",
                HttpStatusCode.BadRequest, 0, string.Empty, 0),
        };

        if (result is double d && (double.IsInfinity(d) || double.IsNaN(d)))
        {
            return null;
        }

        return result;
    }

    private static object? CosineSimilarity(double[] a, double[] b)
    {
        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }
        var denominator = Math.Sqrt(magA) * Math.Sqrt(magB);
        return denominator == 0 ? null : (object)(dot / denominator);
    }

    private static double DotProduct(double[] a, double[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }
        return sum;
    }

    private static double EuclideanDistance(double[] a, double[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var diff = a[i] - b[i];
            sum += diff * diff;
        }
        return Math.Sqrt(sum);
    }

    private static double[]? ToDoubleArray(object? value)
    {
        if (value is double[] dArr) return dArr;
        if (value is float[] fArr) return Array.ConvertAll(fArr, static f => (double)f);
        if (value is IEnumerable<double> dEnum) return dEnum.ToArray();

        JArray? ja = value switch
        {
            JArray arr => arr,
            string s when s.TrimStart().StartsWith("[", StringComparison.Ordinal) => JArray.Parse(s),
            _ => null,
        };
        if (ja is null) return null;

        var result = new double[ja.Count];
        for (var i = 0; i < ja.Count; i++)
        {
            if (ja[i].Type is not (JTokenType.Float or JTokenType.Integer))
                return null;
            result[i] = ja[i].Value<double>();
        }
        return result;
    }
}
