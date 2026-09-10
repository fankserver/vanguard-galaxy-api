using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Owns actionable installation subscriptions separately from synchronous native observations.</summary>
internal sealed class DungeonInstallationEvents : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly List<Installation> _installations = new();
    private readonly List<Delivery> _pending = new();
    private readonly HashSet<Guid> _saves = new();
    private readonly IDisposable _lifecycle;
    private bool _disposed, _draining;
    internal DungeonAegisService Aegis { get; }
    internal DungeonInstallationEvents(LifecycleHub hub)
    {
        _hub = hub;
        Aegis = new DungeonAegisService(hub);
        _lifecycle = hub.Subscribe("vgmodapi.installations", OnLifecycle);
        hub.Services.AfterStopped(Dispose);
    }

    internal Installation Get(string owner, string poiId, ISaveDataRegistration? saveData)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(DungeonInstallationEvents));
        if (string.IsNullOrWhiteSpace(poiId)) throw new ArgumentException("A persistent POI identity is required.", nameof(poiId));
        var installation = new Installation(this, owner, poiId, saveData);
        _installations.Add(installation);
        return installation;
    }

    // Resolve identity at the native boundary; never retain a native location in pending work.
    internal void ExtractionStarted(Guid session, Func<string, bool> matchesLocation)
    {
        _hub.CheckThread();
        if (_disposed || !Current(session)) return;
        foreach (var installation in _installations.ToArray())
        {
            if (installation.Disposed || installation.Handlers.Count == 0) continue;
            try { if (!matchesLocation(installation.PoiId)) continue; }
            catch (Exception error) { Report(installation.Owner, error); continue; }
            if (_disposed || installation.Disposed || !Current(session)) continue;
            foreach (var handler in installation.Handlers.ToArray())
                _pending.Add(new Delivery(session, installation, handler));
        }
    }

    private bool Current(Guid session) => _hub.CurrentSession?.Id == session &&
        _hub.CurrentSession.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;

    private void OnLifecycle(LifecycleEvent fact)
    {
        if (fact.Kind == LifecycleEventKind.SaveStarted && fact.OperationId.HasValue) _saves.Add(fact.OperationId.Value);
        else if (fact.Kind is LifecycleEventKind.SaveSucceeded or LifecycleEventKind.SaveFailed or LifecycleEventKind.SaveSkipped && fact.OperationId.HasValue)
            _saves.Remove(fact.OperationId.Value);
        if (fact.Kind is LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            _pending.RemoveAll(value => value.Session == fact.Session?.Id);
        // Saves can remain on the native stack while a nested callback replaces a session.
        // Only their actual terminal events clear the barrier, never a session transition.
    }

    internal void Tick()
    {
        _hub.CheckThread();
        if (_disposed || _draining || _hub.IsDispatchingCallbacks) return;
        _draining = true;
        try
        {
            var delivered = 0;
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in _pending.ToArray())
            {
                if (!Current(item.Session) || item.Installation.Disposed || !item.Handler.Active)
                { _pending.Remove(item); continue; }
                if (!_hub.SessionTracking.Availability.IsAvailable || !_hub.SaveOutcomes.Availability.IsAvailable)
                {
                    if (!item.Reported)
                    {
                        item.Reported = true;
                        Report(item.Installation.Owner, new InvalidOperationException("Installation reaction is waiting because session or save observation is unavailable."));
                    }
                    continue;
                }
                if (_hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized || _saves.Count != 0) break;
                if (waiting.Contains(item.Installation.Owner)) continue;
                try
                {
                    var registration = item.Installation.SaveData;
                    if (registration != null && (!registration.CanMutate || registration.State.SessionId != item.Session))
                    {
                        // A valid story reaction is retained while this session's data is blocked, not lost.
                        waiting.Add(item.Installation.Owner);
                        if (!item.Reported && registration.State.Kind is SaveDataStateKind.Blocked or SaveDataStateKind.Disposed)
                        {
                            item.Reported = true;
                            Report(item.Installation.Owner, new InvalidOperationException("Installation reaction is waiting for the provider's save data."));
                        }
                        continue;
                    }
                    _pending.Remove(item);
                    // Deliberately not an observational dispatch scope: handlers may perform gameplay actions.
                    item.Handler.Callback();
                }
                catch (Exception error)
                {
                    _pending.Remove(item);
                    Report(item.Installation.Owner, error);
                }
                if (++delivered >= 64) break;
            }
        }
        finally { _draining = false; }
    }

    private void Report(string owner, Exception error)
    {
        using var scope = _hub.EnterServiceDispatch();
        _hub.ReportSubscriberFailure(owner, error);
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true; _lifecycle.Dispose();
        foreach (var installation in _installations.ToArray()) installation.Dispose();
        _pending.Clear(); _saves.Clear();
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
            _events._pending.RemoveAll(item => ReferenceEquals(item.Installation, this));
        }
    }

    internal sealed class Handler
    {
        internal readonly Action Callback;
        internal bool Active = true;
        internal Handler(Action callback) { Callback = callback; }
    }
    private sealed class Delivery
    {
        internal readonly Guid Session;
        internal readonly Installation Installation;
        internal readonly Handler Handler;
        internal bool Reported;
        internal Delivery(Guid session, Installation installation, Handler handler)
        { Session = session; Installation = installation; Handler = handler; }
    }
}
