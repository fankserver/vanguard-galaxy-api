using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Does not advance stale native UI work or clear a different focus target.</summary>
internal sealed class NavigationFocusRoutine : IEnumerator, IDisposable
{
    private readonly IEnumerator _native;
    private readonly Func<bool> _current;
    private readonly Func<object?> _getFocus;
    private readonly Action _clear;
    private readonly Action _finished;
    private readonly object _target;
    private bool _started, _disposed, _moving, _cancel;
    private IEnumerator? _wait;
    internal NavigationFocusRoutine(IEnumerator native, object target, Func<bool> current,
        Func<object?> getFocus, Action clear, Action finished)
    { _native = native; _target = target; _current = current; _getFocus = getFocus; _clear = clear; _finished = finished; }
    public object? Current => _disposed || _wait != null ? null : _native.Current;
    public void Reset() => throw new NotSupportedException();
    public bool MoveNext()
    {
        if (_disposed || _cancel) return false;
        _moving = true;
        try
        {
            if (!_current() || (_started && !ReferenceEquals(_getFocus(), _target))) { _cancel = true; return false; }
            // Poll CustomYieldInstruction/IEnumerator waits ourselves so Unity never bypasses the currentness check.
            if (_wait != null)
            {
                if (_wait.MoveNext()) return true;
                (_wait as IDisposable)?.Dispose(); _wait = null;
            }
            _started = true;
            bool next = _native.MoveNext();
            if (next) _wait = _native.Current as IEnumerator;
            if (!next || !_current()) _cancel = true;
            return next && !_cancel;
        }
        catch { _cancel = true; throw; }
        finally { _moving = false; if (_cancel) Dispose(); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        if (_moving) { _cancel = true; return; }
        _disposed = true;
        try
        {
            try { (_wait as IDisposable)?.Dispose(); }
            finally { _wait = null; (_native as IDisposable)?.Dispose(); }
        }
        finally
        {
            try { if (_started && ReferenceEquals(_getFocus(), _target)) _clear(); }
            finally { _finished(); }
        }
    }
}
