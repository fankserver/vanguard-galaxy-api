using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Identity-scoped installation subscriptions; gameplay delivery is shared with other domain events.</summary>
internal sealed class DungeonInstallationEvents : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly List<Installation> _installations = new();
    private bool _disposed;
    internal DungeonAegisService Aegis { get; }
    internal DungeonInstallationEvents(LifecycleHub hub)
    { _hub = hub; Aegis = new DungeonAegisService(hub); hub.Services.AfterStopped(Dispose); }

    internal Installation Get(string owner, string poiId, ISaveDataRegistration? saveData)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(DungeonInstallationEvents));
        if (string.IsNullOrWhiteSpace(poiId)) throw new ArgumentException("A persistent POI identity is required.", nameof(poiId));
        var installation = new Installation(this, owner, poiId, saveData);
        _installations.Add(installation);
        return installation;
    }

    internal void ExtractionStarted(Guid session, Func<string, bool> matchesLocation)
    {
        _hub.CheckThread();
        if (_disposed) return;
        foreach (var installation in _installations.ToArray())
        {
            if (installation.Disposed || installation.Handlers.Count == 0) continue;
            try { if (!matchesLocation(installation.PoiId)) continue; }
            catch (Exception error) { _hub.Gameplay.Report(installation.Owner, error); continue; }
            foreach (var handler in installation.Handlers.ToArray())
                _hub.Gameplay.Enqueue(session, installation.Owner, handler.Callback,
                    () => !_disposed && !installation.Disposed && handler.Active, installation.SaveData);
        }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        foreach (var installation in _installations.ToArray()) installation.Dispose();
        Aegis.Dispose();
    }

    internal sealed class Installation : IDungeonInstallation, IDisposable
    {
        private readonly DungeonInstallationEvents _events;
        private readonly string _poiId;
        internal readonly string Owner;
        internal readonly ISaveDataRegistration? SaveData;
        internal readonly List<Handler> Handlers = new();
        internal bool Disposed;
        internal Installation(DungeonInstallationEvents events, string owner, string poiId, ISaveDataRegistration? saveData)
        { _events = events; Owner = owner; _poiId = poiId; SaveData = saveData; }
        public string PoiId { get { _events._hub.CheckThread(); return _poiId; } }
        public event Action? ExtractionStarted
        {
            add
            {
                _events._hub.CheckThread();
                if (Disposed) throw new ObjectDisposedException(nameof(IDungeonInstallation));
                if (value != null) foreach (Action callback in value.GetInvocationList()) Handlers.Add(new Handler(callback));
            }
            remove
            {
                _events._hub.CheckThread();
                if (value == null || Disposed) return;
                var removed = value.GetInvocationList();
                for (var start = Handlers.Count - removed.Length; start >= 0; start--)
                {
                    if (!removed.Select((callback, index) => Handlers[start + index].Callback.Equals(callback)).All(match => match)) continue;
                    for (var index = 0; index < removed.Length; index++) Handlers[start + index].Active = false;
                    Handlers.RemoveRange(start, removed.Length); break;
                }
            }
        }
        public IDisposable KeepEnterable()
        {
            _events._hub.CheckThread();
            if (Disposed) throw new ObjectDisposedException(nameof(IDungeonInstallation));
            var declaration = _events.Aegis.Declare(Owner, _poiId);
            _declarations.Add(declaration);
            return declaration;
        }
        private readonly List<IDisposable> _declarations = new();
        public void Dispose()
        {
            _events._hub.CheckThread();
            if (Disposed) return;
            Disposed = true;
            foreach (var handler in Handlers) handler.Active = false;
            foreach (var declaration in _declarations) declaration.Dispose();
            _declarations.Clear();
            Handlers.Clear(); _events._installations.Remove(this);
        }
    }

    internal sealed class Handler
    {
        internal readonly Action Callback;
        internal bool Active = true;
        internal Handler(Action callback) { Callback = callback; }
    }
}
