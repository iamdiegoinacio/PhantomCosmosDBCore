#nullable enable
namespace Phantom.CosmosDB.Core.Application.Crud;

/// <summary>
/// Scope that serialises unique-key validation and the write that follows it. It only takes the
/// store-wide lock when the container actually defines unique keys; otherwise it is a no-op.
/// Must not span an <c>await</c>.
/// </summary>
internal readonly struct UniqueKeyWriteScope : IDisposable
{
    private readonly object? _lockObject;

    private UniqueKeyWriteScope(object lockObject)
    {
        _lockObject = lockObject;
        Monitor.Enter(lockObject);
    }

    public static UniqueKeyWriteScope Enter(DocumentOperationContext context)
        => context.HasUniqueKeys ? new UniqueKeyWriteScope(context.Store.UniqueKeyWriteLock) : default;

    public void Dispose()
    {
        if (_lockObject is not null)
        {
            Monitor.Exit(_lockObject);
        }
    }
}
