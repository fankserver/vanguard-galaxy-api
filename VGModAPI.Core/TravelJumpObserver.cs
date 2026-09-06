using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Preserves every nested Unity yield while letting the travel adapter observe each
/// step. The factory returning an iterator is NOT completion; only observed readiness after
/// forwarding MoveNext is evidence. Fake/Unity scheduling is never accelerated by this wrapper.</summary>
internal sealed class TravelJumpObserver : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Action _observe;
    private bool _ended, _disposed;
    private object? _current;
    internal TravelJumpObserver(IEnumerator inner, Action observe)
    { _inner = inner; _observe = observe; }
    public object? Current => _current;
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        if (!_inner.MoveNext())
        {
            _ended = true; _current = null; return false;
        }
        var value = _inner.Current;
        _current = value is IEnumerator child ? new TravelJumpObserver(child, _observe) : value;
        _observe();
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (_inner as IDisposable)?.Dispose(); } finally { _ended = true; }
    }
}
