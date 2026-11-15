#nullable enable
namespace CosmosDB.InMemoryEmulator.JsTriggers.Runtime;

/// <summary>
/// Immutable sandbox limits applied to every Jint <c>Engine</c> created by the emulator,
/// protecting tests from runaway or infinite-loop scripts.
/// </summary>
/// <param name="Timeout">Maximum wall-clock time a single script execution may take.</param>
/// <param name="MaxStatements">Maximum number of JavaScript statements a single script execution may run.</param>
internal sealed record JintSandboxOptions(TimeSpan Timeout, int MaxStatements)
{
    /// <summary>Default limits: 5 seconds and 10,000 statements.</summary>
    public static JintSandboxOptions Default { get; } = new(TimeSpan.FromSeconds(5), 10_000);
}
