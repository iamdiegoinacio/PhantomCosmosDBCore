#nullable enable
using System.Globalization;
using Phantom.CosmosDB.Core.Domain.Primitives;

namespace Phantom.CosmosDB.Core.Application.Query.Functions;

/// <summary>
/// Evaluates Cosmos SQL mathematical, trigonometric, rounding, and integer/bitwise functions.
/// </summary>
internal static class MathFunctions
{
    public static bool TryEvaluate(string functionName, object?[] args, out object? result)
    {
        switch (functionName)
        {
            case "ABS":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Abs) : null;
                return true;
            case "CEILING":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Ceiling) : null;
                return true;
            case "FLOOR":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Floor) : null;
                return true;
            case "ROUND":
                if (args.Length >= 2 &&
                    double.TryParse(args[0]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var roundVal) &&
                    double.TryParse(args[1]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var roundPrec))
                {
                    var prec = (int)roundPrec;
                    if (prec < 0)
                    {
                        var factor = Math.Pow(10, -prec);
                        result = Math.Round(roundVal / factor, MidpointRounding.AwayFromZero) * factor;
                        return true;
                    }
                    result = Math.Round(roundVal, prec, MidpointRounding.AwayFromZero);
                    return true;
                }
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => Math.Round(v, MidpointRounding.AwayFromZero)) : null;
                return true;
            case "SQRT":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Sqrt) : null;
                return true;
            case "SQUARE":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => v * v) : null;
                return true;
            case "POWER":
                result = args.Length >= 2 ? SqlMathHelpers.ArithmeticOp(args[0], args[1], Math.Pow) : null;
                return true;
            case "EXP":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Exp) : null;
                return true;
            case "LOG":
                if (args.Length >= 2 &&
                    double.TryParse(args[0]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var logVal) &&
                    double.TryParse(args[1]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var logBase))
                {
                    var logResult = Math.Log(logVal, logBase);
                    result = double.IsNaN(logResult) || double.IsInfinity(logResult) ? UndefinedValue.Instance : logResult;
                    return true;
                }
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Log) : null;
                return true;
            case "LOG10":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Log10) : null;
                return true;
            case "SIGN":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => Math.Sign(v)) : null;
                return true;
            case "TRUNC":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Truncate) : null;
                return true;
            case "PI":
                result = Math.PI;
                return true;
            case "SIN":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Sin) : null;
                return true;
            case "COS":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Cos) : null;
                return true;
            case "TAN":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Tan) : null;
                return true;
            case "COT":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => 1.0 / Math.Tan(v)) : null;
                return true;
            case "ASIN":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Asin) : null;
                return true;
            case "ACOS":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Acos) : null;
                return true;
            case "ATAN":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], Math.Atan) : null;
                return true;
            case "ATN2":
                result = args.Length >= 2 ? SqlMathHelpers.ArithmeticOp(args[0], args[1], Math.Atan2) : null;
                return true;
            case "DEGREES":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => v * (180.0 / Math.PI)) : null;
                return true;
            case "RADIANS":
                result = args.Length > 0 ? SqlMathHelpers.MathOp(args[0], static v => v * (Math.PI / 180.0)) : null;
                return true;
            case "RAND":
                result = Random.Shared.NextDouble();
                return true;
            case "NUMBERBIN":
                if (args.Length < 2)
                {
                    result = null;
                    return true;
                }
                if (double.TryParse(args[0]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var nbVal) &&
                    double.TryParse(args[1]?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var binSize) &&
                    binSize > 0)
                {
                    result = Math.Floor(nbVal / binSize) * binSize;
                    return true;
                }
                result = UndefinedValue.Instance;
                return true;
            case "INTADD":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a + b) : null;
                return true;
            case "INTSUB":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a - b) : null;
                return true;
            case "INTMUL":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a * b) : null;
                return true;
            case "INTDIV":
                if (args.Length < 2) { result = UndefinedValue.Instance; return true; }
                var divDividend = SqlMathHelpers.ToLong(args[0]);
                var divDivisor = SqlMathHelpers.ToLong(args[1]);
                result = !divDividend.HasValue || !divDivisor.HasValue || divDivisor.Value == 0
                    ? UndefinedValue.Instance
                    : divDividend.Value / divDivisor.Value;
                return true;
            case "INTMOD":
                if (args.Length < 2) { result = UndefinedValue.Instance; return true; }
                var modDividend = SqlMathHelpers.ToLong(args[0]);
                var modDivisor = SqlMathHelpers.ToLong(args[1]);
                result = !modDividend.HasValue || !modDivisor.HasValue || modDivisor.Value == 0
                    ? UndefinedValue.Instance
                    : modDividend.Value % modDivisor.Value;
                return true;
            case "INTBITAND":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a & b) : null;
                return true;
            case "INTBITOR":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a | b) : null;
                return true;
            case "INTBITXOR":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseOp(args[0], args[1], static (a, b) => a ^ b) : null;
                return true;
            case "INTBITNOT":
                if (args.Length == 0) { result = null; return true; }
                var notVal = SqlMathHelpers.ToLong(args[0]);
                result = notVal.HasValue ? ~notVal.Value : null;
                return true;
            case "INTBITLEFTSHIFT":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseShiftOp(args[0], args[1], isLeft: true) : null;
                return true;
            case "INTBITRIGHTSHIFT":
                result = args.Length >= 2 ? SqlMathHelpers.BitwiseShiftOp(args[0], args[1], isLeft: false) : null;
                return true;
            default:
                result = null;
                return false;
        }
    }
}
