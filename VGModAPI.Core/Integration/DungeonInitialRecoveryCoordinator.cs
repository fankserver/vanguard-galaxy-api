using System;
using System.Collections.Generic;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonInitialRecoveryCoordinator : IDisposable
{
    private readonly DungeonPodPersistence _state;
    private readonly DungeonOperationResumeAdapter _identity;
    private readonly DungeonPodResumeAdapter _pods;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonInitialRecoveryPorts _ports;
    private readonly Action<Exception> _report;
    private readonly Dictionary<object, object?> _pending = new();
    private readonly HashSet<Guid> _attempted = new();
    private readonly List<IDungeonReturnInstance> _owned = new();
    private bool _polling, _disposed;
    private object _generation = new();
    internal DungeonInitialRecoveryCoordinator(DungeonPodPersistence state, DungeonOperationResumeAdapter identity, DungeonPodResumeAdapter pods,
        IBoardingTacticalNativeBindings native, DungeonInitialRecoveryPorts ports, Action<Exception> report)
    { _state = state; _identity = identity; _pods = pods; _native = native; _ports = ports; _report = report; }
    internal bool Queue(object location, object? boardable)
    {
        if (_disposed || !_identity.LocationMarker(location).HasValue) return false;
        _pending[location] = boardable; return true;
    }
    internal void Poll()
    {
        if (_disposed || _polling || !_state.CanMutate) return;
        _polling = true;
        try { PollCurrent(_state.RestoreToken, _generation); }
        finally { _polling = false; }
    }
    private void RequireCurrent(object token, object generation)
    {
        if (_disposed || !_state.CanMutate || !ReferenceEquals(token, _state.RestoreToken) || !ReferenceEquals(generation, _generation))
            throw new InvalidOperationException("Recovery generation changed during reconstruction.");
    }
    private void PollCurrent(object token, object generation)
    {
        foreach (var request in _pending.ToArray())
        {
            var id = _identity.LocationMarker(request.Key);
            if (!id.HasValue || _attempted.Contains(id.Value)) continue;
            var saved = _state.Operation(id.Value); if (saved == null || (saved.TerminalProgress != DungeonTerminalProgress.NotStarted && !saved.MayResumeWalkExtraction)) continue;
            if (request.Value == null && !_ports.ContainsWalkLocation(request.Key)) continue;
            if (request.Value != null && !_ports.IsLiveTarget(request.Value)) continue;
            var recipient = _ports.Resolve(saved.AttackerShipId); if (recipient == null) continue;
            var records = _state.Snapshot.Where(pod => pod.OperationId == saved.Id && pod.Phase is DungeonPodPhase.Docked or DungeonPodPhase.Launching or DungeonPodPhase.Attached).ToArray();
            var donors = new Dictionary<string, object>(StringComparer.Ordinal); var ready = true;
            foreach (var pod in records)
            {
                var donorId = pod.Transport?.DonorShipId;
                if (string.IsNullOrEmpty(donorId) || _pods.Conflicted(pod.Id) || _pods.DataFor(pod.Id) is { } data && _ports.HasLivePod(data)) { ready = false; break; }
                var donor = _ports.Resolve(donorId!); if (donor == null) { ready = false; break; } donors[donorId!] = donor;
            }
            foreach (var reservation in saved.Donors)
            {
                var donor = _ports.Resolve(reservation.ShipId);
                if (donor == null || request.Value == null || _native.Get(donor, "donorActions")?.GetType().FullName == "Source.SpaceShip.Auto.BoardingReinforcementActions") { ready = false; break; }
                donors[reservation.ShipId] = donor;
            }
            if (!ready) continue;
            var simulation = _native.Get(_native.Get(request.Key, "dungeonData"), "savedSimulation");
            var active = saved.NativePhase == "Active" || saved.MayResumeWalkExtraction;
            if (active && (simulation == null || !_ports.SimulationReady(simulation))) continue;
            RequireCurrent(token, generation);
            _attempted.Add(saved.Id); var built = new List<IDungeonReturnInstance>(); object? operation = null;
            try
            {
                operation = _ports.Create(saved, recipient, request.Key, request.Value, active);
                RequireCurrent(token, generation);
                if (!_identity.Resumed(operation) || !_ports.ValidateOperation(operation)) throw new InvalidOperationException("Operation restore validation failed.");
                foreach (var pod in records)
                {
                    if (request.Value == null) throw new InvalidOperationException("Initial pod target unavailable.");
                    var instance = _ports.BuildPod(pod, donors[pod.Transport!.DonorShipId], request.Value, operation, _pods.DataFor(pod.Id));
                    built.Add(instance); RequireCurrent(token, generation); _ports.BindPod(instance, pod.Id);
                }
                foreach (var reservation in saved.Donors)
                {
                    RequireCurrent(token, generation);
                    _native.Call("donorDispatch", operation, donors[reservation.ShipId], new Dictionary<string, int>(reservation.Crew, StringComparer.Ordinal));
                }
                DungeonInitialRelease.Run(request.Value == null,
                    () => { RequireCurrent(token, generation); _native.Call("resumeDocking", operation); },
                    () => { RequireCurrent(token, generation); _ports.Register(operation); },
                    () => { RequireCurrent(token, generation); _ports.Observe(operation); },
                    () => { foreach (var instance in built) { RequireCurrent(token, generation); instance.Activate(); } });
                RequireCurrent(token, generation);
                _owned.AddRange(built); _pending.Remove(request.Key);
            }
            catch (Exception error)
            {
                if (operation != null) try { _ports.Quarantine(operation, error); } catch (Exception cleanup) { Report(cleanup); }
                foreach (var instance in built) DisposeInstance(instance);
                Report(error);
            }
        }
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
    private void DisposeInstance(IDungeonReturnInstance instance) { try { instance.Dispose(); } catch (Exception error) { Report(error); } }
    internal void Clear()
    {
        _generation = new(); var owned = _owned.ToArray(); _owned.Clear(); _pending.Clear(); _attempted.Clear();
        foreach (var instance in owned) DisposeInstance(instance);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }
}
