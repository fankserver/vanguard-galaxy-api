using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class DeferredActionService : IDeferredActionService, IDisposable
{
    internal const int Capacity = 1024;
    internal const int BatchSize = 64;
    private readonly LifecycleHub _hub;
    private readonly List<Entry> _pending = new();
    private readonly HashSet<Guid> _saves = new();
    private readonly IDisposable _subscription;
    private bool _disposed, _draining;

    internal DeferredActionService(LifecycleHub hub)
    {
        _hub = hub;
        _subscription = hub.Subscribe("vgmodapi.actions", Observe);
        hub.Services.AfterStopped(Dispose);
    }

    public IDisposable Defer(string owner, Guid expectedSessionId, Action action, Action<DeferredActionOutcome> completed,
        ISaveDataRegistration? saveData = null, Func<bool>? canRun = null)
    {
        _hub.CheckThread();
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner ID is required.", nameof(owner));
        if (expectedSessionId == Guid.Empty) throw new ArgumentException("An observed session is required.", nameof(expectedSessionId));
        if (action == null) throw new ArgumentNullException(nameof(action));
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        var entry = new Entry(this, owner, expectedSessionId, action, completed, saveData, canRun);
        var rejection = Rejection(entry);
        if (rejection.HasValue) Finish(entry, rejection.Value);
        else if (_pending.Count >= Capacity) Finish(entry, DeferredActionOutcome.QueueFull);
        else _pending.Add(entry);
        return entry;
    }

    private DeferredActionOutcome? Rejection(Entry entry)
    {
        if (_disposed || _hub.Services.IsStopping || !_hub.SessionTracking.Availability.IsAvailable ||
            !_hub.SaveOutcomes.Availability.IsAvailable) return DeferredActionOutcome.Unavailable;
        var session = _hub.CurrentSession;
        if (session == null || session.Id != entry.Session || session.Phase is SessionPhase.Failed or SessionPhase.Invalidated)
            return DeferredActionOutcome.SessionEnded;
        if (entry.SaveData != null)
        {
            var state = entry.SaveData.State;
            if (state.Kind is SaveDataStateKind.Blocked or SaveDataStateKind.Disposed ||
                (state.SessionId.HasValue && state.SessionId != entry.Session)) return DeferredActionOutcome.SaveDataBlocked;
        }
        return null;
    }

    private void Observe(LifecycleEvent fact)
    {
        if (fact.Kind == LifecycleEventKind.SaveStarted && fact.OperationId.HasValue)
            _saves.Add(fact.OperationId.Value);
        else if (fact.Kind is LifecycleEventKind.SaveSucceeded or LifecycleEventKind.SaveFailed or LifecycleEventKind.SaveSkipped && fact.OperationId.HasValue)
            _saves.Remove(fact.OperationId.Value);
        // Terminal session changes drop pending work even if no later Update occurs.
        if (fact.Kind is LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            foreach (var entry in _pending.ToArray())
                if (!entry.Done && entry.Session == fact.Session?.Id) Finish(entry, DeferredActionOutcome.SessionEnded);
    }

    internal void Tick()
    {
        _hub.CheckThread();
        if (_disposed || _draining || _hub.IsDispatchingCallbacks) return;
        _draining = true;
        try
        {
            // Snapshot prevents reaction-generated events from causing an unbounded same-frame drain.
            var batch = _pending.ToArray();
            var executed = 0;
            var waitingOwners = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in batch)
            {
                if (entry.Done) continue;
                try
                {
                    var rejection = Rejection(entry);
                    if (rejection.HasValue) { Finish(entry, rejection.Value); continue; }
                    if (_hub.IsDispatchingCallbacks || _saves.Count != 0 ||
                        _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized) break;
                    // FIFO within an owner, without a waiting consumer starving other owners.
                    if (waitingOwners.Contains(entry.Owner)) continue;
                    if (entry.SaveData != null && !entry.SaveData.CanMutate)
                    { waitingOwners.Add(entry.Owner); continue; }
                    if (entry.CanRun != null)
                    {
                        bool ready;
                        using (_hub.EnterServiceDispatch()) ready = entry.CanRun();
                        if (entry.Done) continue;
                        if (!ready) { waitingOwners.Add(entry.Owner); continue; }
                    }
                    // Predicates can raise events/dispose registrations. Recheck immediately before action.
                    rejection = Rejection(entry);
                    if (rejection.HasValue) { Finish(entry, rejection.Value); continue; }
                    if (_saves.Count != 0 || _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized ||
                        (entry.SaveData != null && !entry.SaveData.CanMutate)) break;
                    entry.Done = true;
                    _pending.Remove(entry);
                    try { entry.Action(); Complete(entry, DeferredActionOutcome.Executed); }
                    catch (Exception error) { Report(entry, error); Complete(entry, DeferredActionOutcome.Faulted); }
                    if (++executed >= BatchSize) break;
                }
                catch (Exception error) { Report(entry, error); Finish(entry, DeferredActionOutcome.Faulted); }
            }
        }
        finally { _draining = false; }
    }

    private void Report(Entry entry, Exception error)
    {
        using var scope = _hub.EnterServiceDispatch();
        _hub.ReportSubscriberFailure(entry.Owner, error);
    }

    private void Finish(Entry entry, DeferredActionOutcome outcome)
    {
        if (entry.Done) return;
        entry.Done = true;
        _pending.Remove(entry);
        Complete(entry, outcome);
    }

    private void Complete(Entry entry, DeferredActionOutcome outcome)
    {
        using var scope = _hub.EnterServiceDispatch();
        try { entry.Completed(outcome); }
        catch (Exception error) { Report(entry, error); }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        _subscription.Dispose();
        foreach (var entry in _pending.ToArray()) Finish(entry, DeferredActionOutcome.Unavailable);
        _saves.Clear();
    }

    private sealed class Entry : IDisposable
    {
        private readonly DeferredActionService _service;
        internal readonly string Owner;
        internal readonly Guid Session;
        internal readonly Action Action;
        internal readonly Action<DeferredActionOutcome> Completed;
        internal readonly ISaveDataRegistration? SaveData;
        internal readonly Func<bool>? CanRun;
        internal bool Done;
        internal Entry(DeferredActionService service, string owner, Guid session, Action action, Action<DeferredActionOutcome> completed,
            ISaveDataRegistration? saveData, Func<bool>? canRun)
        { _service = service; Owner = owner; Session = session; Action = action; Completed = completed; SaveData = saveData; CanRun = canRun; }
        public void Dispose()
        {
            _service._hub.CheckThread();
            _service.Finish(this, DeferredActionOutcome.Cancelled);
        }
    }
}
