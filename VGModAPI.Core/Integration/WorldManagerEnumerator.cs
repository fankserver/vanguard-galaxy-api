using System;
using System.Collections;
using System.IO;

namespace VGModAPI.Core.Integration;

/// <summary>Retains the originating guard across manager yields, including nested enumerators.</summary>
internal sealed class WorldManagerEnumerator : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly object _manager;
    private readonly IWorldLifetimeHookHost _host;
    private bool _ended, _disposed;
    public object? Current { get; private set; }
    internal WorldManagerEnumerator(IEnumerator inner, object manager, IWorldLifetimeHookHost host)
    { _inner = inner; _manager = manager; _host = host; }
    private void Require()
    {
        if (!_host.AllowManager(_manager)) throw new InvalidDataException("Quarantined world manager cannot resume initialization.");
    }
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        try
        {
            Require();
            bool moved = _inner.MoveNext();
            Require();
            if (!moved) { _ended = true; Current = null; return false; }
            var value = _inner.Current;
            Require();
            Current = value is IEnumerator child ? new WorldManagerEnumerator(child, _manager, _host) : value;
            return true;
        }
        catch { _ended = true; Current = null; throw; }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _ended = true; Current = null;
        // Cleanup must not be skipped because admission was revoked.
        (_inner as IDisposable)?.Dispose();
    }
}
