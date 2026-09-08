using System;
using System.Collections;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonCrewObserver
{
    private readonly Func<object, BoardingHandle?> _operationHandle;
    private readonly DungeonSettlementService _settlement;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Action<Exception> _report;
    private readonly List<Scope> _scopes = new();
    internal DungeonCrewObserver(GameBindings game, BoardingObserver boarding, DungeonSettlementService settlement, Action<Exception> report)
        : this(boarding.CommandHandleForOperation, settlement, new BoardingCommandNativeBindings(game, DungeonSettlementBindings.Hooks, DungeonSettlementBindings.Members), report) { }
    internal DungeonCrewObserver(Func<object, BoardingHandle?> operationHandle, DungeonSettlementService settlement, IBoardingTacticalNativeBindings native, Action<Exception> report)
    { _operationHandle = operationHandle; _settlement = settlement; _native = native; _report = report; }
    internal IDisposable? Begin(object operation)
    {
        var handle = _operationHandle(operation); if (handle == null) return null;
        var recipient = _native.Get(_native.Get(operation, "operationShip"), "settlementShipData");
        var scope = new Scope(this, operation, recipient, handle); _scopes.Add(scope); return scope;
    }
    internal void PrisonersApplied(object recipient, string crew, int requested, int overflow)
    {
        if (_scopes.Count == 0 || requested <= 0 || overflow < 0 || overflow > requested) return;
        var scope = _scopes[_scopes.Count - 1]; if (!ReferenceEquals(scope.Recipient, recipient)) return;
        var accepted = requested - overflow; if (accepted == 0) return;
        Guard(() =>
        {
            var state = _settlement.Get(scope.Handle); if (state == null) return;
            var prisoners = new Dictionary<string, int>(state.PrisonersDelivered, StringComparer.Ordinal);
            prisoners.TryGetValue(crew, out var previous); prisoners[crew] = checked(previous + accepted);
            _settlement.ObserveCrew(scope.Handle, Casualties(scope.Operation), prisoners);
        });
    }
    internal void Sample(object operation)
    {
        Guard(() =>
        {
            var handle = _operationHandle(operation); if (handle == null) return;
            var state = _settlement.Get(handle); if (state == null || _native.Get(operation, "simulation") == null) return;
            _settlement.ObserveCrew(handle, Casualties(operation), state.PrisonersDelivered);
        });
    }
    private Dictionary<string, int> Casualties(object operation)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var simulation = _native.Get(operation, "simulation");
        if (_native.Get(simulation, "friendlyUnits") is not IEnumerable units) return result;
        foreach (var unit in units)
        {
            if (_native.Get(unit, "state")?.ToString() != "Killed" && (int)_native.Get(unit, "hp")! > 0) continue;
            var crew = (string)_native.Get(unit, "settlementCrewType")!;
            result.TryGetValue(crew, out var count); result[crew] = checked(count + 1);
        }
        return result;
    }
    private void Guard(Action action)
    { try { action(); } catch (Exception error) { try { _report(error); } catch { } } }
    private sealed class Scope : IDisposable
    {
        private readonly DungeonCrewObserver _owner;
        internal readonly object Operation;
        internal readonly object? Recipient;
        internal readonly BoardingHandle Handle;
        internal Scope(DungeonCrewObserver owner, object operation, object? recipient, BoardingHandle handle)
        { _owner = owner; Operation = operation; Recipient = recipient; Handle = handle; }
        public void Dispose() => _owner._scopes.Remove(this);
    }
}
