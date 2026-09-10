using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Object-owned event registration. Delivery is delegated to the shared gameplay boundary.</summary>
internal sealed class GameplayEvent<T> : IDisposable
{
    private readonly Action _checkThread;
    private readonly List<Handler> _handlers = new();
    private bool _disposed;
    internal GameplayEvent(Action checkThread) { _checkThread = checkThread; }
    internal bool HasSubscribers { get { _checkThread(); return _handlers.Count > 0; } }
    internal void Add(Action<T>? callback)
    {
        _checkThread();
        if (_disposed) throw new ObjectDisposedException(nameof(GameplayEvent<T>));
        if (callback != null) foreach (Action<T> handler in callback.GetInvocationList()) _handlers.Add(new Handler(handler));
    }
    internal void Remove(Action<T>? callback)
    {
        _checkThread();
        if (callback == null) return;
        var removed = callback.GetInvocationList();
        for (var start = _handlers.Count - removed.Length; start >= 0; start--)
        {
            var matches = true;
            for (var index = 0; index < removed.Length; index++)
                if (!_handlers[start + index].Callback.Equals(removed[index])) { matches = false; break; }
            if (!matches) continue;
            for (var index = 0; index < removed.Length; index++) _handlers[start + index].Active = false;
            _handlers.RemoveRange(start, removed.Length); return;
        }
    }
    internal void Publish(LifecycleHub hub, Guid session, string owner, T value, Func<bool> live, ISaveDataRegistration? saveData = null, ISaveDataRegistration? prerequisite = null)
    {
        _checkThread();
        foreach (var handler in _handlers.ToArray())
            hub.Gameplay.Enqueue(session, owner, () => handler.Callback(value),
                () => !_disposed && handler.Active && live(), saveData, prerequisite);
    }
    public void Dispose()
    {
        _checkThread(); if (_disposed) return;
        _disposed = true;
        foreach (var handler in _handlers) handler.Active = false;
        _handlers.Clear();
    }
    private sealed class Handler
    {
        internal readonly Action<T> Callback;
        internal bool Active = true;
        internal Handler(Action<T> callback) { Callback = callback; }
    }
}
