using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Preserves every nested Unity yield while letting the travel adapter observe each
/// step. Children (including Unity's CustomYieldInstruction/WaitUntil IEnumerators) carry ONLY
/// the per-step observe callback; lifecycle (termination) belongs to the root iterator alone.
/// A completed/terminated child must never cancel the pending jump leg. Disposal of an in-flight
/// root iterator is replacement/cancellation, not a successful arrival.</summary>
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
        // Children are driven by the caller (as Unity drives yielded IEnumerators) and are
        // observed per-step only; they never carry the root lifecycle callback.
        _current = value is IEnumerator child ? new TravelJumpObserver(child, _observe) : value;
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
