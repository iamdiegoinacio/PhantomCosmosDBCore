#nullable enable
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;

namespace CosmosDB.InMemoryEmulator.JsTriggers.Runtime;

/// <summary>
/// Discovers and invokes the entry-point function of a Cosmos DB script body, which may be
/// either a named function declaration or an anonymous function expression.
/// </summary>
internal static partial class JintFunctionInvoker
{
    [GeneratedRegex(@"^function\s+(\w+)\s*\(")]
    private static partial Regex LeadingNamedFunction();

    [GeneratedRegex(@"\bfunction\s+(\w+)\s*\(")]
    private static partial Regex AnyNamedFunction();

    /// <summary>
    /// Invokes a stored procedure body: a leading named declaration is executed then invoked by
    /// name; anything else is evaluated as an anonymous function expression.
    /// </summary>
    public static JsValue InvokeStoredProcedure(Engine engine, string jsBody, JsValue[] args)
        => InvokeNamedOrAnonymous(engine, jsBody, args, LeadingNamedFunction().Match(jsBody.TrimStart()));

    /// <summary>
    /// Invokes a UDF body: the first named declaration found anywhere in the body is used;
    /// otherwise the body is evaluated as an anonymous function expression.
    /// </summary>
    public static JsValue InvokeUdf(Engine engine, string jsBody, JsValue[] args)
        => InvokeNamedOrAnonymous(engine, jsBody, args, AnyNamedFunction().Match(jsBody));

    /// <summary>
    /// Executes a trigger body and, when it declares a named function, invokes the first one
    /// (triggers receive their input through <c>getContext()</c>, not arguments).
    /// </summary>
    public static void InvokeTrigger(Engine engine, string jsBody)
    {
        engine.Execute(jsBody);

        var match = AnyNamedFunction().Match(jsBody);
        if (match.Success)
        {
            engine.Invoke(match.Groups[1].Value);
        }
    }

    private static JsValue InvokeNamedOrAnonymous(Engine engine, string jsBody, JsValue[] args, Match namedFunction)
    {
        if (namedFunction.Success)
        {
            engine.Execute(jsBody);
            return engine.Invoke(namedFunction.Groups[1].Value, args);
        }

        var anonymous = engine.Evaluate($"({jsBody})");
        return engine.Invoke(anonymous, args);
    }
}
