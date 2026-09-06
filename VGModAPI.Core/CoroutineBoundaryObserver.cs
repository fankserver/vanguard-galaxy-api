using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Wraps a bound coroutine iterator so the adapter observes the first step and the
/// verified completion boundary (MoveNext returning false, or disposal before completion).
/// A factory returning an iterator is not itself completion; only after forwarding MoveNext is
/// the boundary real.</summary>
internal sealed class CoroutineBoundaryObserver : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Action? _onFirst;
    private readonly Action? _onDone;
    private bool _first = true, _ended, _disposed;
    private object? _current;
    internal CoroutineBoundaryObserver(IEnumerator inner, Action? onFirst = null, Action? onDone = null)
    { _inner = inner; _onFirst = onFirst; _onDone = onDone; }
    public object? Current => _current;
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        if (!_inner.MoveNext())
        {
            _ended = true; _current = null; _onDone?.Invoke(); return false;
        }
        var value = _inner.Current;
        _current = value is IEnumerator child ? new CoroutineBoundaryObserver(child, _onFirst, _onDone) : value;
        if (_first) { _first = false; _onFirst?.Invoke(); }
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (_inner as IDisposable)?.Dispose(); }
        finally { if (!_ended) { _ended = true; _onDone?.Invoke(); } }
    }
}
