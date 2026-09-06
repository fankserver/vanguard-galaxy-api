using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Wraps a bound dock/undock coroutine iterator so the adapter observes the root's
/// FIRST step and its SUCCESSFUL root completion (MoveNext returning false). Nested children
/// (procedures, WaitUntil) carry no lifecycle callback, so they never emit premature
/// DockedPhysical/Undocking/Leaving. Disposal is cancellation, never a successful completion:
/// it must not fire onDone. Callers re-verify the physical docking state themselves.</summary>
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
        // Children preserve yields but carry no onFirst/onDone; lifecycle is root-only.
        _current = value is IEnumerator child ? new CoroutineBoundaryObserver(child) : value;
        if (_first) { _first = false; _onFirst?.Invoke(); }
        return true;
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (_inner as IDisposable)?.Dispose(); }
        catch { /* disposal is never a successful completion boundary */ }
        // No onDone: a stopped/disposed coroutine is cancellation, not completion.
    }
}
