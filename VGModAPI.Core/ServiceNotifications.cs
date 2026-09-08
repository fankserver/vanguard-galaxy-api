using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Main-thread event delivery with independent handlers and CLR multicast removal semantics.</summary>
internal sealed class ServiceNotifications<T> : IDisposable
{
    private sealed class Slot
    {
        internal readonly Action<T> Callback;
        internal bool Active = true;
        internal Slot(Action<T> callback) { Callback = callback; }
    }

    private readonly Action _checkThread;
    private readonly Action<string, Exception> _report;
    private readonly Func<IDisposable> _enterDispatch;
    private readonly List<Slot> _handlers = new();
    private readonly Queue<T> _pending = new();
    private bool _dispatching, _disposed, _completing;

    internal ServiceNotifications(Action checkThread, Action<string, Exception> report, Func<IDisposable> enterDispatch)
    { _checkThread = checkThread; _report = report; _enterDispatch = enterDispatch; }

    internal void Add(Action<T>? callback)
    {
        _checkThread();
        if (_disposed || _completing) throw new ObjectDisposedException(nameof(ServiceNotifications<T>));
        if (callback == null) return;
        foreach (Action<T> handler in callback.GetInvocationList()) _handlers.Add(new Slot(handler));
    }

    internal void Remove(Action<T>? callback)
    {
        _checkThread();
        if (callback == null || _disposed) return;
        var removed = callback.GetInvocationList();
        for (var start = _handlers.Count - removed.Length; start >= 0; start--)
        {
            var matches = true;
            for (var index = 0; index < removed.Length; index++)
                if (!_handlers[start + index].Callback.Equals(removed[index])) { matches = false; break; }
            if (!matches) continue;
            for (var index = 0; index < removed.Length; index++) _handlers[start + index].Active = false;
            _handlers.RemoveRange(start, removed.Length);
            return;
        }
    }

    internal void Publish(T value)
    {
        _checkThread();
        if (_disposed) return;
        _pending.Enqueue(value);
        if (_dispatching) return;
        _dispatching = true;
        try
        {
            using var scope = _enterDispatch();
            while (!_disposed && _pending.Count != 0)
            {
                var next = _pending.Dequeue();
                foreach (var slot in _handlers.ToArray())
                {
                    if (!slot.Active) continue;
                    try { slot.Callback(next); }
                    catch (Exception error)
                    {
                        try { _report(slot.Callback.Method.Module.Assembly.GetName().Name + ":" + slot.Callback.Method.Name, error); }
                        catch { /* Diagnostics must not interrupt notification delivery. */ }
                    }
                }
            }
        }
        finally
        {
            _dispatching = false;
            if (_completing) Dispose();
        }
    }

    internal void Complete(T terminal)
    {
        _checkThread();
        if (_disposed || _completing) return;
        _completing = true;
        Publish(terminal);
        if (!_dispatching) Dispose();
    }

    public void Dispose()
    {
        _checkThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _handlers) slot.Active = false;
        _handlers.Clear(); _pending.Clear();
    }
}
