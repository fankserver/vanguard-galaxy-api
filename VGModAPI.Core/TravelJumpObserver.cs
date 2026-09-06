using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Preserves every nested Unity yield while letting the travel adapter observe each
/// step, and reports iterator termination (normal completion or replacement disposal). The
/// factory returning an iterator is NOT completion; only observed readiness after forwarding
/// MoveNext is evidence. Fake/Unity scheduling is never accelerated by this wrapper.</summary>
internal sealed class TravelJumpObserver : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Action _observe;
    private readonly Action? _terminated;
    private bool _ended, _disposed;
    private object? _current;
    internal TravelJumpObserver(IEnumerator inner, Action observe, Action? terminated = null)
    { _inner = inner; _observe = observe; _terminated = terminated; }
    public object? Current => _current;
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        if (!_inner.MoveNext())
        {
            _ended = true; _current = null; _terminated?.Invoke(); return false;
        }
        var value = _inner.Current;
        _current = value is IEnumerator child ? new TravelJumpObserver(child, _observe, _terminated) : value;
        _observe();
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (_inner as IDisposable)?.Dispose(); }
        finally { if (!_ended) { _ended = true; _terminated?.Invoke(); } }
    }
}
