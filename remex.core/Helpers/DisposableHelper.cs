namespace Remex.Core.Helpers;

/// <summary>
/// Composite disposable pattern for managing multiple IDisposable resources.
/// </summary>
public sealed class CompositeDisposable : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly object _lock = new();
    private bool _disposed;

    public void Add(IDisposable disposable)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CompositeDisposable));

            _disposables.Add(disposable);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        // Dispose in reverse order (LIFO - last added, first disposed)
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                _disposables[i]?.Dispose();
            }
            catch
            {
                // Continue disposing even if one fails
                // Consider logging here in production
            }
        }

        _disposables.Clear();
    }
}

/// <summary>
/// Base class for disposable types with proper pattern implementation.
/// </summary>
public abstract class DisposableBase : IDisposable
{
    private bool _disposed;

    ~DisposableBase()
    {
        Dispose(disposing: false);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // Dispose managed resources
            DisposeManagedResources();
        }

        // Dispose unmanaged resources (if any)
        DisposeUnmanagedResources();

        _disposed = true;
    }

    /// <summary>
    /// Override to dispose managed resources (e.g., other IDisposable objects).
    /// </summary>
    protected virtual void DisposeManagedResources()
    {
        // Override in derived classes
    }

    /// <summary>
    /// Override to dispose unmanaged resources (e.g., handles, pointers).
    /// </summary>
    protected virtual void DisposeUnmanagedResources()
    {
        // Override in derived classes if needed
    }

    /// <summary>
    /// Throws ObjectDisposedException if this instance has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }

    /// <summary>
    /// Gets whether this instance has been disposed.
    /// </summary>
    protected bool IsDisposed => _disposed;
}
