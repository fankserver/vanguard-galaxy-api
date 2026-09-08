using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>One native subscription per delegate preserves ordering with legacy subscribers.</summary>
internal sealed class ServiceSubscriptions<T> : IDisposable
{
    private sealed class Slot
    {
        internal readonly Action<T> Callback;
        internal IDisposable? Lease;
        internal bool Active = true;
        internal Slot(Action<T> callback) { Callback = callback; }
        internal void Close() { Active = false; Lease?.Dispose(); }
    }
    private readonly LifecycleHub _hub;
    private readonly Func<string, Action<T>, IDisposable>? _subscribe;
    private readonly Func<T, bool> _deliver;
    private readonly Func<bool> _available;
    private readonly List<Slot> _slots = new();
    private bool _disposed;

    internal ServiceSubscriptions(LifecycleHub hub, Func<string, Action<T>, IDisposable>? subscribe,
        Func<T, bool> deliver, Func<bool> available)
    { _hub = hub; _subscribe = subscribe; _deliver = deliver; _available = available; }

    internal void Add(Action<T>? callback)
    {
        _hub.CheckThread();
        if (_disposed || _hub.Services.IsStopping) throw new ObjectDisposedException(nameof(ServiceSubscriptions<T>));
        if (callback == null) return;
        var added = new List<Slot>();
        try
        {
            foreach (Action<T> handler in callback.GetInvocationList())
            {
                var slot = new Slot(handler);
                added.Add(slot);
                if (_subscribe == null) continue;
                try
                {
                    slot.Lease = _subscribe(handler.Method.Module.Assembly.GetName().Name ?? "service subscriber", value =>
                    {
                        if (!slot.Active || !_deliver(value)) return;
                        using var scope = _hub.EnterServiceDispatch();
                        handler(value);
                    });
                }
                catch (ObjectDisposedException) when (!_available())
                { /* A terminally unavailable source has no remaining observations. */ }
            }
            _slots.AddRange(added);
        }
        catch
        {
            foreach (var slot in added) slot.Close();
            throw;
        }
    }

    internal void Remove(Action<T>? callback)
    {
        _hub.CheckThread();
        if (_disposed || callback == null) return;
        var removed = callback.GetInvocationList();
        for (var start = _slots.Count - removed.Length; start >= 0; --start)
        {
            var matches = true;
            for (var index = 0; index < removed.Length; ++index)
                if (!_slots[start + index].Callback.Equals(removed[index])) { matches = false; break; }
            if (!matches) continue;
            for (var index = 0; index < removed.Length; ++index) _slots[start + index].Close();
            _slots.RemoveRange(start, removed.Length);
            return;
        }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots) slot.Close();
        _slots.Clear();
    }
}
