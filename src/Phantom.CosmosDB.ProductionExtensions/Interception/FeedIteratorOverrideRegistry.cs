#nullable enable
namespace Phantom.CosmosDB.ProductionExtensions.Interception;

/// <summary>
/// Holds the feed-iterator override delegates and decides which one is active.
/// </summary>
/// <remarks>
/// <para>
/// <b>AsyncLocal factory</b> — per-async-flow and therefore test-isolated: parallel tests each see
/// their own delegate. It flows through <c>await</c>, <c>Task.Run</c> and anything capturing
/// <see cref="ExecutionContext"/>, but <b>not</b> through <c>new Thread()</c>.
/// </para>
/// <para>
/// <b>Static fallback factory</b> — a <c>volatile</c> global consulted only when the AsyncLocal factory
/// is <c>null</c> (typically a bare thread). It is safe because the delegate is stateless: it simply
/// materialises whichever queryable it receives, so tests do not cross-contaminate. Edge case: if one test
/// deregisters while another test's <c>new Thread()</c> is mid-flight, that thread falls back to the real
/// SDK; this is a pre-existing isolation limitation, not one introduced by the fallback.
/// </para>
/// <para>
/// When neither delegate is set (production) <see cref="Resolve"/> returns <c>null</c> and the caller
/// delegates to the real Cosmos SDK.
/// </para>
/// </remarks>
internal static class FeedIteratorOverrideRegistry
{
    private static readonly AsyncLocal<Func<object, object>?> AsyncLocalFactory = new();
    private static volatile Func<object, object>? _staticFallbackFactory;

    /// <summary>Per-async-flow override. Backed by <see cref="AsyncLocal{T}"/>.</summary>
    public static Func<object, object>? PerAsyncFlowFactory
    {
        get => AsyncLocalFactory.Value;
        set => AsyncLocalFactory.Value = value;
    }

    /// <summary>Global override for threads where <see cref="AsyncLocal{T}"/> does not flow.</summary>
    public static Func<object, object>? StaticFallbackFactory
    {
        get => _staticFallbackFactory;
        set => _staticFallbackFactory = value;
    }

    /// <summary>
    /// Resolution order: per-async-flow factory, then static fallback, otherwise <c>null</c>
    /// (meaning "no override — use the real SDK").
    /// </summary>
    public static Func<object, object>? Resolve() => PerAsyncFlowFactory ?? StaticFallbackFactory;
}
