#nullable enable
using CosmosDB.InMemoryEmulator.ProductionExtensions.Interception;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;

namespace CosmosDB.InMemoryEmulator.ProductionExtensions;

/// <summary>
/// Provides a drop-in replacement for the Cosmos SDK's <c>.ToFeedIterator()</c> that can be
/// intercepted at test time — without requiring any test-specific libraries in production code.
///
/// <h3>The problem</h3>
/// The Cosmos SDK's <c>.ToFeedIterator()</c> only works on queryables created by the SDK's own
/// LINQ provider (<c>CosmosLinqQueryProvider</c>). When you use an in-memory <see cref="Container"/>
/// in component tests, <c>GetItemLinqQueryable&lt;T&gt;()</c> returns a standard LINQ-to-Objects
/// queryable. Calling <c>.ToFeedIterator()</c> on that throws:
/// <c>ArgumentOutOfRangeException: ToFeedIterator is only supported on Cosmos LINQ query operations</c>.
///
/// <h3>The solution</h3>
/// Replace <c>.ToFeedIterator()</c> with <c>.ToFeedIteratorOverridable()</c> in production code.
/// This single-token change is the only production-side modification required.
///
/// <h3>How the override mechanism works</h3>
/// <c>ToFeedIteratorOverridable</c> checks two factory delegates before falling back to the real SDK:
///
/// <list type="number">
///   <item>
///     <term>AsyncLocal factory (<see cref="FeedIteratorFactory"/>)</term>
///     <description>
///       Backed by <see cref="AsyncLocal{T}"/>. Each async flow gets its own value, so parallel
///       tests don't interfere with each other. This is the primary interception point. It flows
///       correctly through <c>await</c>, <c>Task.Run</c>, <c>ThreadPool.QueueUserWorkItem</c>,
///       and anything else that captures <see cref="System.Threading.ExecutionContext"/>.
///     </description>
///   </item>
///   <item>
///     <term>Static fallback factory (<see cref="StaticFallbackFactory"/>)</term>
///     <description>
///       A plain <c>volatile static</c> field. This exists as a safety net for code that uses
///       <c>new Thread()</c>, which does <b>not</b> capture <c>ExecutionContext</c> and therefore
///       does not inherit <c>AsyncLocal</c> values. When the AsyncLocal factory is null (because
///       we're on a bare thread), the static fallback catches it. Because the factory is stateless
///       — it simply materialises whatever <c>IQueryable&lt;T&gt;</c> it receives — different tests
///       passing different queryables (pointing at different in-memory data) won't cross-contaminate.
///     </description>
///   </item>
///   <item>
///     <term>Real SDK (<c>.ToFeedIterator()</c>)</term>
///     <description>
///       If both factories are null, this is production — delegate to the real Cosmos SDK as normal.
///     </description>
///   </item>
/// </list>
///
/// <h3>Who sets these factories?</h3>
/// <c>InMemoryFeedIteratorSetup.Register()</c> (in the emulator library) sets <b>both</b> factories
/// to the same delegate. <c>Deregister()</c> clears both. You never need to set them manually.
///
/// <h3>Production behaviour</h3>
/// In production, neither factory is set. <c>ToFeedIteratorOverridable</c> calls
/// <c>.ToFeedIterator()</c> — identical to what you had before. No overhead beyond a null check.
///
/// <h3>Example — production code (the only change needed)</h3>
/// <code>
/// // Before:
/// var iterator = container
///     .GetItemLinqQueryable&lt;MyEntity&gt;()
///     .Where(item => item.IsActive)
///     .ToFeedIterator();                   // ← fails with in-memory container
///
/// // After:
/// var iterator = container
///     .GetItemLinqQueryable&lt;MyEntity&gt;()
///     .Where(item => item.IsActive)
///     .ToFeedIteratorOverridable();         // ← works in both production and tests
/// </code>
///
/// <h3>Example — test setup</h3>
/// <code>
/// // In your WebApplicationFactory or test fixture constructor — one line:
/// InMemoryFeedIteratorSetup.Register();
///
/// // Teardown (optional — clears both factories):
/// InMemoryFeedIteratorSetup.Deregister();
/// </code>
/// </summary>
[Obsolete("No longer needed since 4.0. All recommended approaches (InMemoryCosmos, UseInMemoryCosmosDB, UseInMemoryCosmosContainers) use FakeCosmosHandler which handles .ToFeedIterator() natively. Use .ToFeedIterator() instead of .ToFeedIteratorOverridable().")]
public static class CosmosOverridableFeedIteratorExtensions
{
    /// <summary>
    /// Per-async-flow factory delegate for creating feed iterators in tests.
    ///
    /// Backed by <see cref="AsyncLocal{T}"/>: each async flow (i.e. each test) gets its own
    /// value, enabling safe parallel test execution. Flows through <c>await</c>,
    /// <c>Task.Run</c>, and anything that captures <see cref="System.Threading.ExecutionContext"/>.
    ///
    /// Does <b>not</b> flow to <c>new Thread()</c> — see <see cref="StaticFallbackFactory"/>.
    ///
    /// When null, <see cref="ToFeedIteratorOverridable{T}"/> checks <see cref="StaticFallbackFactory"/>
    /// before falling through to the real SDK's <c>.ToFeedIterator()</c>.
    ///
    /// Set by <c>InMemoryFeedIteratorSetup.Register()</c>. You should not need to set this directly.
    /// </summary>
    public static Func<object, object>? FeedIteratorFactory
    {
        get => FeedIteratorOverrideRegistry.PerAsyncFlowFactory;
        set => FeedIteratorOverrideRegistry.PerAsyncFlowFactory = value;
    }

    /// <summary>
    /// Global fallback factory for threads where <see cref="AsyncLocal{T}"/> does not flow.
    ///
    /// The primary scenario: production code that uses <c>new Thread(() => { ... }).Start()</c>.
    /// <c>new Thread()</c> does not capture <c>ExecutionContext</c>, so the <see cref="AsyncLocal{T}"/>
    /// value (<see cref="FeedIteratorFactory"/>) will be null on that thread. This static fallback
    /// catches that case.
    ///
    /// Checked only when <see cref="FeedIteratorFactory"/> is null. If both are null, we're in
    /// production and <c>.ToFeedIterator()</c> is called normally.
    ///
    /// This field is <c>volatile</c> for safe cross-thread visibility — no lock needed because
    /// reads and writes of reference types are atomic on .NET.
    ///
    /// Set by <c>InMemoryFeedIteratorSetup.Register()</c>. You should not need to set this directly.
    /// </summary>
    public static Func<object, object>? StaticFallbackFactory
    {
        get => FeedIteratorOverrideRegistry.StaticFallbackFactory;
        set => FeedIteratorOverrideRegistry.StaticFallbackFactory = value;
    }

    /// <summary>
    /// Drop-in replacement for <c>.ToFeedIterator()</c> that supports in-memory interception.
    ///
    /// <para><b>Resolution order:</b></para>
    /// <list type="number">
    ///   <item>
    ///     <see cref="FeedIteratorFactory"/> (AsyncLocal, per-async-flow) — handles the vast
    ///     majority of test scenarios where ExecutionContext flows normally.
    ///   </item>
    ///   <item>
    ///     <see cref="StaticFallbackFactory"/> (static, global) — catches <c>new Thread()</c>
    ///     and other contexts where AsyncLocal doesn't flow.
    ///   </item>
    ///   <item>
    ///     <c>queryable.ToFeedIterator()</c> — real Cosmos SDK. This is the production path,
    ///     taken when neither factory is set.
    ///   </item>
    /// </list>
    /// </summary>
    /// <typeparam name="T">The document type being queried.</typeparam>
    /// <param name="queryable">
    /// The LINQ queryable obtained from <c>container.GetItemLinqQueryable&lt;T&gt;()</c>,
    /// optionally with further LINQ operators (<c>.Where()</c>, <c>.OrderBy()</c>, etc.).
    /// </param>
    /// <returns>
    /// A <see cref="FeedIterator{T}"/> that pages through the query results. In tests this is
    /// an <c>InMemoryFeedIterator&lt;T&gt;</c>; in production it's the real SDK iterator.
    /// </returns>
    public static FeedIterator<T> ToFeedIteratorOverridable<T>(this IQueryable<T> queryable)
    {
        // Check AsyncLocal first (test-isolated, per-async-flow), then static fallback
        // (catches new Thread() where AsyncLocal doesn't flow). If both are null, we're
        // in production — delegate to the real Cosmos SDK.
        ArgumentNullException.ThrowIfNull(queryable);

        var factory = FeedIteratorOverrideRegistry.Resolve();

        if (factory is not null)
        {
            return (FeedIterator<T>)factory(queryable);
        }

        return queryable.ToFeedIterator();
    }
}
