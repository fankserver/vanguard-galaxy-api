using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class BoardingCombatAdapter
{
    private readonly LifecycleHub _hub;
    private readonly BoardingCombatService _rules;
    private readonly Func<object, string, object?> _read;
    private readonly Action<object, string, object?> _write;
    private readonly BoardingCommandNativeBindings? _native;
    private readonly List<Scope> _scopes = new();
    private readonly List<MoraleScope> _morale = new();
    internal BoardingCombatAdapter(LifecycleHub hub, BoardingCombatService rules, GameBindings game)
    {
        _hub = hub; _rules = rules;
        var bindings = new BoardingCommandNativeBindings(game, additionalMembers: BoardingCombatBindings.Members);
        _native = bindings;
        _read = (obj, key) => bindings.Get(obj, key); _write = bindings.Set;
    }
    internal BoardingCombatAdapter(LifecycleHub hub, BoardingCombatService rules, Func<object, string, object?> read, Action<object, string, object?> write)
    { _hub = hub; _rules = rules; _read = read; _write = write; }
    internal bool AllowPlayerReinforcement(object simulation) => Allow(simulation, BoardingCombatPolicyKind.Reinforcement, BoardingCombatSide.Attackers);
    internal bool AllowPanelReinforcement(object panel)
    {
        if (_native == null) return true;
        var location = _native.Get(panel, "panelLocation"); var manager = _native.Manager;
        if (location == null || manager == null) return false;
        var operation = _native.Call("commandGetOperation", manager, location);
        var simulation = _native.Get(operation, "simulation");
        return simulation != null && AllowPlayerReinforcement(simulation);
    }
    private Guid? Session
    {
        get { var session = _hub.CurrentSession; return session?.Phase is SessionPhase.PlayerReady or SessionPhase.GameplayInitialized ? session.Id : null; }
    }
    internal object? Begin(object simulation)
    {
        _hub.CheckThread(); if (!Session.HasValue) return null;
        var scope = new Scope(simulation, Session.Value); _scopes.Add(scope); return scope;
    }
    internal void End(object? state) { if (state is Scope scope) _scopes.Remove(scope); }
    private BoardingCombatContext Context(object simulation, BoardingCombatPolicyKind kind, BoardingCombatSide side, int? room, float value)
        => new(new(Session!.Value, _read(simulation, "combatKind")!.ToString() == "HostileShip" ? BoardingEncounterKind.Ship : BoardingEncounterKind.Installation,
            (int)_read(simulation, "combatLevel")!), kind, side, room, value);
    private BoardingCombatSide Side(object unit) => _read(unit, "combatFriendly") is true ? BoardingCombatSide.Attackers : BoardingCombatSide.Defenders;
    internal float UnitValue(object unit, BoardingCombatPolicyKind kind, float value)
    {
        _hub.CheckThread(); var scope = _scopes.LastOrDefault(s => s.Session == Session);
        if (scope == null || !Finite(value)) return value;
        var context = Context(scope.Simulation, kind, Side(unit), null, value);
        var result = _rules.Scale(context); return Session == context.Encounter.SessionId ? result : value;
    }
    internal float Casualties(object simulation, int room, float rate, bool friendly)
    {
        _hub.CheckThread(); if (!Session.HasValue || !Finite(rate)) return rate;
        var context = Context(simulation, BoardingCombatPolicyKind.CasualtyRate, friendly ? BoardingCombatSide.Attackers : BoardingCombatSide.Defenders, room >= 0 ? room : null, rate);
        var result = _rules.Scale(context); return Session == context.Encounter.SessionId ? result : rate;
    }
    internal bool Allow(object simulation, BoardingCombatPolicyKind kind, BoardingCombatSide side, object? unit = null, int? room = null)
    {
        _hub.CheckThread(); if (!Session.HasValue) return true;
        if (unit != null) ApplyPendingMorale(simulation, unit);
        var context = Context(simulation, kind, side, room, 1);
        var allowed = _rules.Allow(context); return Session != context.Encounter.SessionId || allowed;
    }
    internal object? BeginMorale(object simulation)
    {
        _hub.CheckThread(); if (!Session.HasValue) return null;
        var scope = new MoraleScope(simulation, Session.Value);
        foreach (var key in new[] { "friendlyUnits", "hostileUnits" })
            foreach (var unit in (IList)_read(simulation, key)!)
                if (unit != null && !scope.Before.ContainsKey(unit)) scope.Before.Add(unit, (float)_read(unit, "combatMorale")!);
        _morale.Add(scope); return scope;
    }
    internal void ApplyPendingMorale(object simulation, object unit)
    {
        var scope = _morale.LastOrDefault(s => ReferenceEquals(s.Simulation, simulation) && s.Session == Session);
        if (scope != null) ApplyMorale(scope, unit);
    }
    private void ApplyMorale(MoraleScope scope, object unit)
    {
        if (scope.Session != Session || !scope.Before.TryGetValue(unit, out var before) || !scope.Applied.Add(unit)) return;
        var after = (float)_read(unit, "combatMorale")!;
        if (!Finite(before) || !Finite(after)) return;
        var difference = after - before;
        var context = Context(scope.Simulation, BoardingCombatPolicyKind.Morale, Side(unit), null, Math.Abs(difference));
        var scaled = _rules.Scale(context);
        if (Session != scope.Session) return;
        var value = Math.Max(0, Math.Min(1, before + (difference < 0 ? -scaled : scaled)));
        _write(unit, "combatMorale", value);
    }
    internal void EndMorale(object? state, bool succeeded)
    {
        if (state is not MoraleScope scope) return;
        try { if (succeeded) foreach (var unit in scope.Before.Keys) ApplyMorale(scope, unit); }
        finally { _morale.Remove(scope); }
    }
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0;
    private sealed class Scope
    {
        internal readonly object Simulation;
        internal readonly Guid Session;
        internal Scope(object simulation, Guid session) { Simulation = simulation; Session = session; }
    }
    private sealed class MoraleScope
    {
        internal readonly object Simulation;
        internal readonly Guid Session;
        internal readonly Dictionary<object, float> Before = new();
        internal readonly HashSet<object> Applied = new();
        internal MoraleScope(object simulation, Guid session) { Simulation = simulation; Session = session; }
    }
}
