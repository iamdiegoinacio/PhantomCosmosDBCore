#nullable enable
using Jint;

namespace CosmosDB.InMemoryEmulator.JsTriggers.Runtime;

/// <summary>
/// Factory Method for sandboxed Jint <see cref="Engine"/> instances. Single place that
/// decides how every script (stored procedure, trigger, UDF) is constrained.
/// </summary>
internal static class JintEngineFactory
{
    /// <summary>Creates an engine constrained by <see cref="JintSandboxOptions.Default"/>.</summary>
    public static Engine Create() => Create(JintSandboxOptions.Default);

    /// <summary>Creates an engine constrained by the supplied <paramref name="sandbox"/> limits.</summary>
    public static Engine Create(JintSandboxOptions sandbox)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        return new Engine(options =>
        {
            options.TimeoutInterval(sandbox.Timeout);
            options.MaxStatements(sandbox.MaxStatements);
        });
    }
}
