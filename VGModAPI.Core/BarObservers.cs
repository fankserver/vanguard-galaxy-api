using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed partial class BarContentService
{
    private sealed class Observer : IDisposable
    {
        private readonly BarContentService _service;
        internal readonly Action<BarRosterFinalized> Callback;
        internal readonly string Owner;
        internal bool Active = true;
        internal Observer(BarContentService service, string owner, Action<BarRosterFinalized> callback) { _service = service; Owner = owner; Callback = callback; }
        public void Dispose() { _service._checkThread(); Active = false; _service._observers.Remove(this); }
    }
    private readonly List<Observer> _observers = new();
    private bool _publishing;

    internal IDisposable Subscribe(string owner, Action<BarRosterFinalized> callback)
    {
        _checkThread();
        if (_disposed) throw new ObjectDisposedException(nameof(BarContentService));
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Observer identity required.", nameof(owner));
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        if (_observers.Count >= 128) throw new InvalidOperationException("Bar observer limit reached.");
        var observer = new Observer(this, owner, callback);
        _observers.Add(observer);
        return observer;
    }

    internal bool Publish(BarRosterFinalized snapshot, Func<bool> current)
    {
        _checkThread();
        if (_disposed || _publishing) return false;
        _publishing = true;
        try
        {
            if (!current()) return false;
            foreach (var observer in _observers.ToArray())
            {
                if (_disposed || !current()) break;
                if (!observer.Active) continue;
                try { observer.Callback(snapshot); }
                catch (Exception error) { try { _reportObserver?.Invoke(observer.Owner, error); } catch { } }
            }
            return true;
        }
        finally { _publishing = false; }
    }
}
