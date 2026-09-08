using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonInitialRecoveryRuntime : IDisposable
{
    private readonly DungeonRecoveryRuntime _owner;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonRecoveryWorld _world;
    private readonly DungeonInitialOperationFactory _operations;
    private readonly DungeonReturnPodFactory _pods;
    private readonly Action<Exception> _report;
    private readonly Dictionary<object, object?> _pending = new();
    private readonly HashSet<Guid> _attempted = new();
    private readonly List<DungeonReturnPodInstance> _owned = new();
    internal DungeonInitialRecoveryRuntime(DungeonRecoveryRuntime owner, IBoardingTacticalNativeBindings native, DungeonRecoveryWorld world,
        DungeonInitialOperationFactory operations, DungeonReturnPodFactory pods, Action<Exception> report)
    { _owner = owner; _native = native; _world = world; _operations = operations; _pods = pods; _report = report; }
    internal bool Queue(object location, object? boardable)
    {
        if (!_owner.Operations.LocationMarker(location).HasValue) return false;
        _pending[location] = boardable; return true;
    }
    internal void Poll()
    {
        if (!_owner.State.CanMutate) return;
        foreach (var request in _pending.ToArray())
        {
            var id = _owner.Operations.LocationMarker(request.Key);
            if (!id.HasValue || _attempted.Contains(id.Value)) continue;
            var saved = _owner.State.Operation(id.Value); if (saved == null || saved.TerminalProgress != DungeonTerminalProgress.NotStarted) continue;
            if (request.Value == null && !_world.ContainsWalkLocation(request.Key)) continue;
            if (request.Value != null && (request.Value is not Component target || !target)) continue;
            var recipient = _world.Resolve(saved.AttackerShipId); if (recipient == null) continue;
            var records = _owner.State.Snapshot.Where(pod => pod.OperationId == saved.Id && pod.Phase is DungeonPodPhase.Docked or DungeonPodPhase.Launching or DungeonPodPhase.Attached).ToArray();
            var donors = new Dictionary<string, object>(StringComparer.Ordinal); var ready = true;
            foreach (var pod in records)
            {
                var donorId = pod.Transport?.DonorShipId;
                if (string.IsNullOrEmpty(donorId) || _owner.Pods.Conflicted(pod.Id) || _owner.Pods.DataFor(pod.Id) is { } data && _world.HasLivePod(data)) { ready = false; break; }
                var donor = _world.Resolve(donorId!); if (donor == null) { ready = false; break; } donors[donorId!] = donor;
            }
            foreach (var reservation in saved.Donors)
            {
                var donor = _world.Resolve(reservation.ShipId);
                if (donor == null || request.Value == null || _native.Get(donor, "donorActions")?.GetType().FullName == "Source.SpaceShip.Auto.BoardingReinforcementActions") { ready = false; break; }
                donors[reservation.ShipId] = donor;
            }
            if (!ready) continue;
            var simulation = _native.Get(_native.Get(request.Key, "dungeonData"), "savedSimulation");
            var active = saved.NativePhase == "Active";
            if (active && (simulation == null || _owner.SimulationReady?.Invoke(simulation) == false)) continue;
            _attempted.Add(saved.Id); var built = new List<DungeonReturnPodInstance>(); object? operation = null;
            try
            {
                operation = _operations.Create(saved, recipient, request.Key, request.Value, active);
                if (!_owner.Operations.Resumed(operation) || !_owner.OperationReady(operation) || _owner.ValidateInitialOperation?.Invoke(operation) == false) throw new InvalidOperationException("Operation restore validation failed.");
                foreach (var pod in records)
                {
                    if (request.Value == null) throw new InvalidOperationException("Initial pod target unavailable.");
                    var instance = new DungeonReturnPodInstance(_pods.BuildInitial(pod, donors[pod.Transport!.DonorShipId], request.Value, operation, _owner.Pods.DataFor(pod.Id)));
                    built.Add(instance); _owner.BindInitialPod(instance, pod.Id);
                }
                foreach (var reservation in saved.Donors)
                    _native.Call("donorDispatch", operation, donors[reservation.ShipId], new Dictionary<string, int>(reservation.Crew, StringComparer.Ordinal));
                DungeonInitialRelease.Run(request.Value == null,
                    () => _native.Call("resumeDocking", operation), () => _operations.Register(operation),
                    () => (_owner.ObserveInitialOperation ?? throw new InvalidOperationException("Boarding observation unavailable."))(operation),
                    () => { foreach (var instance in built) instance.Activate(); });
                _owned.AddRange(built); _pending.Remove(request.Key);
            }
            catch (Exception error)
            { if (operation != null) _owner.Quarantine(operation, error); foreach (var instance in built) instance.Dispose(); try { _report(error); } catch { } }
        }
    }
    internal void Clear() { foreach (var instance in _owned) instance.Dispose(); _owned.Clear(); _pending.Clear(); _attempted.Clear(); }
    public void Dispose() => Clear();
}
