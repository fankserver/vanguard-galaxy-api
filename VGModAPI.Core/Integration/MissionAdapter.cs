using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class MissionAdapter : IDisposable
{
    internal sealed class Call
    {
        internal readonly string Kind;
        internal readonly object Mission, Player;
        internal readonly Guid Session;
        internal readonly MissionTransitions.Observation Observation;
        internal readonly bool BeforeActive, BeforeFailed, CompletedRemoval;
        internal readonly int ArchiveBefore;
        internal readonly string? Definition;
        internal readonly string Name;
        internal readonly string[] Tags;
        internal bool WitnessedInsertion, RewardRemoval, Closed;
        internal Call(string kind, object mission, object player, Guid session, MissionTransitions.Observation observation,
            MissionBindings bindings, bool flag)
        {
            Kind = kind; Mission = mission; Player = player; Session = session; Observation = observation;
            BeforeActive = bindings.Contains(player, mission); BeforeFailed = bindings.Failed(mission);
            Definition = bindings.Definition(mission); Name = bindings.Name(mission); Tags = bindings.Tags(mission);
            ArchiveBefore = bindings.ArchiveCount(player, Definition);
            CompletedRemoval = kind == "remove" && flag;
        }
    }
    private readonly LifecycleHub _hub;
    private readonly MissionBindings _bindings;
    private readonly Action<Exception> _report;
    private readonly IDisposable _subscription;
    private readonly List<Call> _calls = new();
    private object? _player;
    private Guid? _session;
    private volatile bool _faulted;
    private bool _reconciled, _disposed;
    internal MissionTransitions Events { get; }
    internal MissionAdapter(LifecycleHub hub, MissionBindings bindings, Action<Exception> report)
    {
        _hub = hub; _bindings = bindings; _report = report;
        Events = new MissionTransitions((_, error) => report(error));
        _subscription = hub.Subscribe("vgmodapi.missions", e => Guard(() => ObserveLifecycle(e)));
    }
    internal void Guard(Action action)
    {
        if (_faulted || _disposed) return;
        try { _hub.CheckThread(); action(); }
        catch (Exception error) { _faulted = true; try { _report(error); } catch { } }
    }
    internal void Poll()
    {
        _hub.CheckThread();
        if (_faulted && !_reconciled)
        {
            _reconciled = true; Clear();
            _hub.SetCapability("mission-transitions", false, "Mission observer fault; restart required.");
        }
    }
    private void Clear() { _player = null; _session = null; _calls.Clear(); Events.Reset(null); }
    private void ObserveLifecycle(LifecycleEvent e)
    {
        if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
        { Clear(); return; }
        if (e.Kind != LifecycleEventKind.PlayerReady || e.Session == null) return;
        Clear(); _player = _bindings.Player; _session = e.Session.Id; Events.Reset(_session);
        if (_player == null) throw new InvalidOperationException("Ready mission player missing.");
        using var observation = Events.Begin();
        foreach (var mission in _bindings.Active(_player))
            Events.Record(observation, mission, MissionTransitionKind.Restored, new MissionFacts(false, true),
                _bindings.Definition(mission), _bindings.Name(mission), _bindings.Tags(mission));
    }
    private bool Current => _session.HasValue && _hub.CurrentSession is { } session && session.Id == _session
        && session.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized && ReferenceEquals(_player, _bindings.Player);
    internal Call? Begin(string kind, object? player, object? mission, bool flag = false, string? definition = null)
    {
        if (!Current || (player != null && !ReferenceEquals(player, _player))) return null;
        if (kind == "archive" && string.IsNullOrEmpty(definition)) return null;
        if (kind == "archive") mission = _calls.LastOrDefault(call => call.Kind == "remove" && call.CompletedRemoval && call.Definition == definition)?.Mission;
        if (mission == null || !_bindings.IsMission(mission)) return null;
        bool active = _bindings.Contains(_player!, mission);
        foreach (var pending in _calls.Where(call => call.Kind == "accept" && ReferenceEquals(call.Mission, mission)))
            pending.WitnessedInsertion |= active;
        var observation = Events.Begin();
        try
        {
            var call = new Call(kind, mission, _player!, _session!.Value, observation, _bindings, flag);
            _calls.Add(call); return call;
        }
        catch { observation.Dispose(); throw; }
    }
    internal void End(Call call)
    {
        if (call.Closed) return;
        try
        {
            if (!Current || call.Session != _session || !ReferenceEquals(call.Player, _player)) return;
            bool active = _bindings.Contains(call.Player, call.Mission), failed = _bindings.Failed(call.Mission);
            var facts = new MissionFacts(call.BeforeActive, active, call.BeforeFailed, failed, call.RewardRemoval,
                call.ArchiveBefore, _bindings.ArchiveCount(call.Player, call.Definition));
            MissionTransitionKind? kind = null;
            switch (call.Kind)
            {
                case "accept":
                    facts = new MissionFacts(call.BeforeActive, active || call.WitnessedInsertion);
                    kind = MissionTransitionKind.Accepted; break;
                case "claim": kind = MissionTransitionKind.Completed; break;
                case "fail": kind = MissionTransitionKind.Failed; break;
                case "archive": kind = MissionTransitionKind.Archived; break;
                case "remove":
                    var claim = _calls.LastOrDefault(parent => parent.Kind == "claim" && ReferenceEquals(parent.Mission, call.Mission));
                    if (call.BeforeActive && !active && call.CompletedRemoval && claim != null) claim.RewardRemoval = true;
                    else kind = !call.CompletedRemoval && !failed ? MissionTransitionKind.Abandoned : MissionTransitionKind.Removed;
                    break;
            }
            if (kind.HasValue) Events.Record(call.Observation, call.Mission, kind.Value, facts, call.Definition, call.Name, call.Tags);
        }
        finally
        {
            int index = _calls.IndexOf(call);
            if (index >= 0) { foreach (var pending in _calls.Skip(index)) pending.Closed = true; _calls.RemoveRange(index, _calls.Count - index); }
            call.Closed = true; call.Observation.Dispose();
        }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        Clear(); _disposed = true; _subscription.Dispose(); Events.Dispose();
    }
}
