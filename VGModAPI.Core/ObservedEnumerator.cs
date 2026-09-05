using System;
using System.Collections;

namespace VGModAPI.Core;

/// <summary>Preserves nested yields while carrying attempt context during each MoveNext/Dispose.</summary>
internal sealed class ObservedEnumerator : IEnumerator, IDisposable
{
    private readonly IEnumerator _inner;
    private readonly Func<IDisposable> _enter;
    private readonly Action<Exception> _failed;
    private readonly Action? _completed;
    private bool _ended;
    private bool _disposed;
    private object? _current;

    internal ObservedEnumerator(IEnumerator inner, Func<IDisposable> enter, Action<Exception> failed, Action? completed = null)
    { _inner = inner; _enter = enter; _failed = failed; _completed = completed; }
    public object? Current => _current;
    public bool MoveNext()
    {
        if (_ended || _disposed) return false;
        using var scope = _enter();
        try
        {
            if (!_inner.MoveNext())
            {
                _ended = true;
                _current = null;
                _completed?.Invoke();
                return false;
            }
            var value = _inner.Current;
            _current = value is IEnumerator child
                ? new ObservedEnumerator(child, _enter, _failed) : value;
            return true;
        }
        catch (Exception ex)
        {
            _ended = true;
            _failed(ex);
            throw;
        }
    }
    public void Reset() => throw new NotSupportedException();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using var scope = _enter();
        try { (_inner as IDisposable)?.Dispose(); }
        catch (Exception ex) { _failed(ex); throw; }
        finally
        {
            if (!_ended && _completed != null)
                _failed(new OperationCanceledException("Observed load iterator was disposed before completion."));
            _ended = true;
        }
    }
}
