#nullable enable
using Jint;
using Jint.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Phantom.CosmosDB.JsTriggers.Runtime;

/// <summary>
/// Adapter between CLR values and Jint <see cref="JsValue"/>s. All C# &lt;-&gt; JavaScript
/// conversions of the JsTriggers package go through this class.
/// </summary>
internal static class JintValueMarshaller
{
    private const string SerializeSlot = "__toSerialize";

    /// <summary>Converts CLR arguments into JavaScript values bound to <paramref name="engine"/>.</summary>
    public static JsValue[] ToJsValues(Engine engine, object?[]? args)
    {
        var jsArgs = new JsValue[args?.Length ?? 0];
        for (var i = 0; i < jsArgs.Length; i++)
        {
            jsArgs[i] = ToJsValue(engine, args![i]);
        }

        return jsArgs;
    }

    /// <summary>Converts a single CLR value into a JavaScript value (JSON round-trip for complex types).</summary>
    public static JsValue ToJsValue(Engine engine, object? value) => value switch
    {
        null => JsValue.Null,
        string text => new JsString(text),
        int number => new JsNumber(number),
        long number => new JsNumber(number),
        double number => new JsNumber(number),
        bool flag => flag ? JsBoolean.True : JsBoolean.False,
        _ => FromJson(engine, JsonConvert.SerializeObject(value))
    };

    /// <summary>Evaluates a JSON document as a JavaScript object literal inside <paramref name="engine"/>.</summary>
    public static JsValue FromJson(Engine engine, string json) => engine.Evaluate($"({json})");

    /// <summary>
    /// Serializes a JavaScript value with <c>JSON.stringify</c> using the same sandboxed engine.
    /// Returns <c>null</c> when the value has no JSON representation (e.g. <c>undefined</c>).
    /// </summary>
    public static string? ToJson(Engine engine, JsValue value)
    {
        engine.SetValue(SerializeSlot, value);
        var serialized = engine.Evaluate($"JSON.stringify({SerializeSlot})");
        return serialized.IsUndefined() ? null : serialized.AsString();
    }

    /// <summary>Converts a UDF result into a CLR primitive or a <see cref="JToken"/> for objects/arrays.</summary>
    public static object? ToClr(Engine engine, JsValue value)
    {
        if (value.IsNull() || value.IsUndefined()) return null;
        if (value.IsBoolean()) return value.AsBoolean();
        if (value.IsNumber()) return value.AsNumber();
        if (value.IsString()) return value.AsString();

        var json = ToJson(engine, value);
        return json is null ? null : JToken.Parse(json);
    }

    /// <summary>
    /// Produces the stored-procedure response string: objects/arrays stay JSON, while a plain
    /// JavaScript string is unwrapped from its JSON quotes to mimic the Cosmos DB response body.
    /// </summary>
    public static string? ToStoredProcedureResponse(Engine engine, JsValue value)
    {
        var json = ToJson(engine, value);
        if (json is not null && json.StartsWith('"') && json.EndsWith('"'))
        {
            return json[1..^1]
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\t", "\t");
        }

        return json;
    }
}
