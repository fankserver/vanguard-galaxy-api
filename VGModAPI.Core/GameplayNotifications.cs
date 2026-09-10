using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Internal delivery boundary shared by actionable domain events. Never exposed as a consumer scheduler.</summary>
internal sealed class GameplayNotifications : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IDisposable _lifetime;
    private readonly List<Delivery> _pending = new();
    private readonly HashSet<Guid> _saves = new();
    private bool _disposed, _draining;
    internal GameplayNotifications(LifecycleHub hub)
    {
        _hub = hub;
        _lifetime = hub.Subscribe("vgmodapi.gameplay-events", OnLifecycle);
        hub.Services.AfterStopped(Dispose);
    }

    internal void Enqueue(Guid session, string owner, Action callback, Func<bool> subscribed, ISaveDataRegistration? saveData = null, ISaveDataRegistration? prerequisite = null)
    {
        _hub.CheckThread();
        if (_disposed || !Current(session) || !subscribed()) return;
        _pending.Add(new Delivery(session, owner, callback, subscribed, saveData, prerequisite));
    }

    private bool Current(Guid session) => _hub.CurrentSession?.Id == session &&
        _hub.CurrentSession.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized;
    private void OnLifecycle(LifecycleEvent fact)
    {
        if (fact.Kind == LifecycleEventKind.SaveStarted && fact.OperationId.HasValue) _saves.Add(fact.OperationId.Value);
        else if (fact.Kind is LifecycleEventKind.SaveSucceeded or LifecycleEventKind.SaveFailed or LifecycleEventKind.SaveSkipped && fact.OperationId.HasValue)
            _saves.Remove(fact.OperationId.Value);
        if (fact.Kind is LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            _pending.RemoveAll(item => item.Session == fact.Session?.Id);
        // Replacement can occur while a save is still on the native stack: only its terminal event releases it.
    }

    internal void Tick()
    {
        _hub.CheckThread();
        if (_disposed || _draining || _hub.IsDispatchingCallbacks) return;
        _draining = true;
        try
        {
            var invoked = 0;
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in _pending.ToArray())
            {
                try
                {
                    if (_disposed || !Current(item.Session) || !item.Subscribed())
                    { _pending.Remove(item); continue; }
                    if (!_hub.SessionTracking.Availability.IsAvailable || !_hub.SaveOutcomes.Availability.IsAvailable)
                    { DiagnoseWait(item, "Gameplay reaction is waiting for session or save observation."); continue; }
                    if (_hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized || _saves.Count != 0) break;
                    if (waiting.Contains(item.Owner)) continue;
                    var blocked = false;
                    foreach (var registration in item.SaveData)
                    {
                        if (registration.CanMutate && registration.State.SessionId == item.Session) continue;
                        waiting.Add(item.Owner); blocked = true;
                        if (registration.State.Kind is SaveDataStateKind.Blocked or SaveDataStateKind.Disposed)
                            DiagnoseWait(item, "Gameplay reaction is waiting for its custom save data.");
                        break;
                    }
                    if (blocked) continue;
                    _pending.Remove(item);
                    item.Callback();
                }
                catch (Exception error) { _pending.Remove(item); Report(item.Owner, error); }
                if (++invoked >= 64) break;
            }
        }
        finally { _draining = false; }
    }

    private void DiagnoseWait(Delivery item, string message)
    {
        if (item.Reported) return;
        item.Reported = true;
        Report(item.Owner, new InvalidOperationException(message));
    }
    internal void Report(string owner, Exception error)
    {
        using var scope = _hub.EnterServiceDispatch();
        _hub.ReportSubscriberFailure(owner, error);
    }
    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true; _lifetime.Dispose(); _pending.Clear(); _saves.Clear();
    }
    private sealed class Delivery
    {
        internal readonly Guid Session;
        internal readonly string Owner;
        internal readonly Action Callback;
        internal readonly Func<bool> Subscribed;
        internal readonly List<ISaveDataRegistration> SaveData = new();
        internal bool Reported;
        internal Delivery(Guid session, string owner, Action callback, Func<bool> subscribed, ISaveDataRegistration? saveData, ISaveDataRegistration? prerequisite)
        {
            Session = session; Owner = owner; Callback = callback; Subscribed = subscribed;
            if (saveData != null) SaveData.Add(saveData);
            if (prerequisite != null && !ReferenceEquals(prerequisite, saveData)) SaveData.Add(prerequisite);
        }
    }
}
