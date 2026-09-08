using System;
using System.Linq;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class DungeonRecoveryRuntime : IDisposable
{
    internal DungeonPodPersistence State { get; }
    internal DungeonPodResumeAdapter Pods { get; }
    internal DungeonOperationResumeAdapter Operations { get; }
    internal DungeonPodReturnObserver ReturnObserver { get; }
    private readonly DungeonReturnRecoveryCoordinator _returns;
    private readonly IDisposable _lifetime;
    private readonly Action<Exception> _report;
    private readonly DungeonMarkerJson _podJson, _operationJson;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Type _shipType;
    internal Func<object, Guid?>? ContentOccurrence { get; set; }
    internal Func<object, bool>? SimulationReady { get; set; }
    internal bool OperationReady(object operation) => DungeonOperationMutationGate.Allows(operation, _native, SimulationReady);
    private System.Runtime.CompilerServices.ConditionalWeakTable<object, Exception> _captureFaults = new();
    private System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _podOwners = new();
    internal DungeonRecoveryRuntime(LifecycleHub hub, IPersistenceApi persistence, GameBindings game, Action<Exception> report)
    {
        _report = report;
        _podJson = new(game.Assembly, "vgmodapiDungeonPod"); _operationJson = new(game.Assembly, "vgmodapiDungeonOperation");
        var native = new BoardingCommandNativeBindings(game, DungeonPodResumeBindings.Methods.Concat(DungeonPodReturnBindings.Hooks).ToArray(), DungeonPodResumeBindings.Members);
        _native = native; _shipType = game.Assembly.GetType("Behaviour.Unit.SpaceShip", true)!;
        var factory = new DungeonReturnPodFactory(game.Assembly, native); var world = new DungeonRecoveryWorld(game.Assembly, native);
        State = new(hub, persistence); Pods = new(State, native); Operations = new(State, native);
        ReturnObserver = new(State, Pods, native, ship => ((Component)ship).transform, world.Ready, Operations.OperationId);
        _returns = new(State, world.Resolve,
            (pod, operation, recipient) => new DungeonReturnPodInstance(factory.Build(pod, recipient, operation.DungeonType, operation.Autonomous, Pods.DataFor(pod.Id))),
            (instance, id) =>
            {
                var pod = (DungeonReturnPodInstance)instance;
                var saved = State.Get(id) ?? throw new InvalidOperationException("Return obligation changed during reconstruction.");
                Pods.Loaded(pod.Data, id);
                if (Pods.Conflicted(id)) throw new InvalidOperationException("Duplicate restored pod identity.");
                Operations.BindReturnCarrier(pod.Operation, saved.OperationId);
                _podOwners.Add(pod.Pod, pod.Operation);
                Pods.DetachSource(pod.Data);
            }, report, saved => !Pods.Conflicted(saved.Id) && (Pods.DataFor(saved.Id) is not { } data || !world.HasLivePod(data)));
        _lifetime = hub.Subscribe("vgmodapi.dungeon-recovery-runtime", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _returns.Poll(); Pods.Clear(); Operations.Clear(); _captureFaults = new(); _podOwners = new(); }
        });
    }
    internal object? ExistingOperation(object? target)
    {
        if (target == null) return null;
        var location = target.GetType().FullName == BindingCatalog.BoardingLocation ? target : _native.Get(target, "data");
        return location == null ? null : _native.Call("commandGetOperation", _native.Manager, location);
    }
    internal bool ObserveOperation(object operation, bool fresh = false)
    {
        if (_captureFaults.TryGetValue(operation, out _) || !OperationReady(operation)) return false;
        try { return CaptureOperation(operation, fresh); }
        catch (Exception error)
        { _captureFaults.Add(operation, error); try { _report(error); } catch { } return false; }
    }
    internal bool CaptureOperation(object operation, bool fresh = false)
    {
        if (DungeonReturnCarrier.IsSettlementOnly(operation)) return false;
        if (!State.CanMutate) return false;
        var location = _native.Get(operation, "location") ?? throw new InvalidOperationException("Missing operation location.");
        var id = Operations.OperationId(operation);
        if (!id.HasValue)
        {
            if (!fresh && Operations.LocationMarker(location).HasValue)
            { if (!Operations.Resumed(operation)) return false; id = Operations.OperationId(operation); }
            else
            {
                var token = (string?)_native.Get(location, "resumeCaptureToken") ?? "";
                var mission = (string?)_native.Get(_native.Get(location, "shipData"), "resumeMissionGuid") ?? "";
                id = Operations.Created(operation, ContentOccurrence?.Invoke(location), token.Length == 0 ? mission : token + (mission.Length == 0 ? "" : "|" + mission));
            }
        }
        if (!id.HasValue || Operations.Conflicted(id.Value)) return false;
        var previous = State.Operation(id.Value)!;
        var phase = _native.Get(operation, "phase")?.ToString() ?? previous.NativePhase;
        var outcome = previous.TerminalProgress == DungeonTerminalProgress.NotStarted ? _native.Get(_native.Get(operation, "simulation"), "outcome")?.ToString() ?? previous.Outcome : previous.Outcome;
        if (!State.TrackOperation(new(previous.Id, previous.LocationId, previous.ContentOccurrence, previous.AttackerShipId, previous.DungeonType,
            phase, outcome, previous.MissionProtection, previous.TerminalProgress, previous.Autonomous))) return false;
        Pods.TrackLocation(location);
        var pending = (System.Collections.IList)_native.Get(operation, "resumePendingPods")!;
        foreach (var pod in (System.Collections.IEnumerable)_native.Get(operation, "_activePods")!)
        {
            if (pod is not Component component || !component) continue;
            var parent = _native.Get(pod, "resumeParentTransform") as Transform;
            var donor = parent ? parent!.GetComponent(_shipType) : null;
            var parentId = (string?)_native.Get(_native.Get(donor, "resumeShipData"), "resumeShipGuid") ?? "";
            if (parentId.Length == 0) throw new InvalidOperationException("Pod donor identity unavailable.");
            var data = _native.Get(pod, "resumePodData")!; var savedId = Pods.IdentityFor(data);
            var originalDonor = savedId.HasValue ? State.Get(savedId.Value)?.Transport?.DonorShipId : null;
            var transport = Pods.CaptureTransport(pod, pending.Contains(pod), value => { var v = (Vector2)value; return (v.x, v.y); }, originalDonor ?? parentId);
            if (!Pods.Observe(pod, id.Value, parentId, true, transport)) return false;
            if (!_podOwners.TryGetValue(pod, out _)) _podOwners.Add(pod, operation);
            if (Pods.IdentityFor(data) is { } tracked) MarkLive(tracked);
        }
        return true;
    }
    internal bool CanAttach(object pod)
    {
        var data = _native.Get(pod, "resumePodData");
        if (data == null || !Pods.IdentityFor(data).HasValue) return true;
        return State.CanMutate && _podOwners.TryGetValue(pod, out var operation) && OperationReady(operation);
    }
    internal bool CanArrive(object pod)
    {
        var data = _native.Get(pod, "resumePodData");
        if (data == null || !Pods.IdentityFor(data).HasValue) return true;
        return _podOwners.TryGetValue(pod, out var operation) && OperationReady(operation) && ReturnObserver.CanArrive(operation, pod);
    }
    internal void LoadPod(object data, object json)
    { if (_podJson.ReadStrict(json) is { } id) Pods.Loaded(data, id); }
    internal void SavePod(object data, object json)
    {
        if (Pods.IdentityFor(data) is not { } id) return;
        if (State.Get(id) == null || Pods.Conflicted(id)) throw new InvalidOperationException("Cannot save unresolved pod state.");
        _podJson.Write(json, id);
    }
    internal void LoadLocation(object location, object json)
    {
        if (_operationJson.ReadStrict(json) is { } id) Operations.LoadedLocation(location, id);
        Pods.TrackLocation(location);
    }
    internal void SaveLocation(object location, object json)
    {
        if (Operations.LocationMarker(location) is not { } id) return;
        if (State.Operation(id) == null || Operations.Conflicted(id)) throw new InvalidOperationException("Cannot save unresolved operation state.");
        _operationJson.Write(json, id);
    }
    internal void Checkpoint()
    {
        State.EnsureSerializationAllowed();
        _returns.Checkpoint((id, instance) =>
        {
            var pod = (DungeonReturnPodInstance)instance;
            var previous = State.Get(id) ?? throw new InvalidOperationException("Missing live return state.");
            var transport = Pods.CaptureTransport(pod.Pod, previous.Transport!.PendingReinforcement,
                value => { var v = (Vector2)value; return (v.x, v.y); }, previous.Transport.DonorShipId);
            if (!State.RefreshTransportPose(id, transport.Pose)) throw new InvalidOperationException("Unable to checkpoint live return pose.");
        });
    }
    internal void Poll()
    { try { _returns.Poll(); } catch (Exception error) { try { _report(error); } catch { } } }
    internal void MarkLive(Guid id) => _returns.MarkLive(id);
    public void Dispose() { _lifetime.Dispose(); _returns.Dispose(); Pods.Clear(); Operations.Clear(); State.Dispose(); }
}
