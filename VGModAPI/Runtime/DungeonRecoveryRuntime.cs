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
    private readonly DungeonInitialRecoveryCoordinator _initial;
    private readonly DungeonRecoveryWorld _world;
    private readonly DungeonLiveTransportIndex _liveTransports = new(value => value is UnityEngine.Object native && native);
    private System.Runtime.CompilerServices.ConditionalWeakTable<object, DonorOwnership> _donors = new();
    private sealed class DonorOwnership
    {
        internal readonly Guid Operation; internal readonly string ShipId; internal readonly object Ship, Token;
        internal DonorOwnership(Guid operation, string shipId, object ship, object token) { Operation = operation; ShipId = shipId; Ship = ship; Token = token; }
    }
    private readonly IDisposable _lifetime;
    private readonly Action<Exception> _report;
    private readonly DungeonMarkerJson _podJson, _operationJson;
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly Type _shipType;
    private readonly DungeonOperationOptionsAdapter _options;
    internal Func<object, Guid?>? ContentOccurrence { get; set; }
    internal Func<object, bool>? SimulationReady { get; set; }
    internal Func<object, bool>? ValidateInitialOperation { get; set; }
    internal Action<object>? ObserveInitialOperation { get; set; }
    internal void Quarantine(object operation, Exception error)
    { _captureFaults.Remove(operation); _captureFaults.Add(operation, error); }
    internal bool OperationReady(object operation) => !_captureFaults.TryGetValue(operation, out _) && DungeonOperationMutationGate.Allows(operation, _native, SimulationReady);
    private System.Runtime.CompilerServices.ConditionalWeakTable<object, Exception> _captureFaults = new();
    private System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _podOwners = new();
    internal DungeonRecoveryRuntime(LifecycleHub hub, IPersistenceApi persistence, GameBindings game, Action<Exception> report)
    {
        _report = report;
        _podJson = new(game.Assembly, "vgmodapiDungeonPod"); _operationJson = new(game.Assembly, "vgmodapiDungeonOperation");
        var native = new BoardingCommandNativeBindings(game, DungeonPodResumeBindings.Methods.Concat(DungeonPodReturnBindings.Hooks).ToArray(), DungeonPodResumeBindings.Members);
        _native = native; _shipType = game.Assembly.GetType("Behaviour.Unit.SpaceShip", true)!;
        var optionsType = game.Assembly.GetType(BindingCatalog.BoardingOptions, true)!;
        var ammoType = game.Assembly.GetType("Source.Dungeon.AmmoType", true)!; var stealthType = game.Assembly.GetType("Source.Dungeon.StealthMode", true)!;
        _options = new(native, () => Activator.CreateInstance(optionsType)!, (key, name) =>
        {
            var type = key == "ammo" ? ammoType : stealthType; var value = Enum.Parse(type, name);
            return Enum.IsDefined(type, value) ? value : throw new InvalidOperationException("Unsupported saved option enum.");
        });
        var factory = new DungeonReturnPodFactory(game.Assembly, native); var world = new DungeonRecoveryWorld(game.Assembly, native); _world = world;
        State = new(hub, persistence); Pods = new(State, native); Operations = new(State, native);
        ReturnObserver = new(State, Pods, native, ship => ((Component)ship).transform, world.Ready, Operations.OperationId);
        RefundHooks = new(State, ReturnObserver, value => ObserveOperation(value), Operations.OperationId);
        _returns = new(State, world.Resolve,
            (pod, operation, recipient) => new DungeonReturnPodInstance(factory.Build(pod, recipient, operation.DungeonType, operation.Autonomous, Pods.DataFor(pod.Id))),
            (instance, id) =>
            {
                var pod = (DungeonReturnPodInstance)instance;
                var saved = State.Get(id) ?? throw new InvalidOperationException("Return obligation changed during reconstruction.");
                Pods.Loaded(pod.Data, id);
                if (Pods.Conflicted(id)) throw new InvalidOperationException("Duplicate restored pod identity.");
                Operations.BindReturnCarrier(pod.Operation, saved.OperationId);
                _podOwners.Add(pod.Pod, pod.Operation); _liveTransports.Track(id, pod.Pod);
                Pods.DetachSource(pod.Data);
            }, report, saved => !Pods.Conflicted(saved.Id) && (Pods.DataFor(saved.Id) is not { } data || !world.HasLivePod(data)));
        var initialFactory = new DungeonInitialOperationFactory(game.Assembly, native, _options);
        _initial = new(State, Operations, Pods, native, new DungeonInitialRecoveryPorts
        {
            ContainsWalkLocation = world.ContainsWalkLocation, IsLiveTarget = value => value is Component component && component,
            HasLivePod = world.HasLivePod, Resolve = world.Resolve,
            SimulationReady = value => SimulationReady?.Invoke(value) ?? true,
            ValidateOperation = operation => OperationReady(operation) && (ValidateInitialOperation?.Invoke(operation) ?? true),
            Create = initialFactory.Create,
            BuildPod = (saved, donor, target, operation, data) => new DungeonReturnPodInstance(factory.BuildInitial(saved, donor, target, operation, data)),
            BindPod = (instance, id) => BindInitialPod((DungeonReturnPodInstance)instance, id), Register = initialFactory.Register,
            Observe = operation =>
            {
                if (!ObserveOperation(operation)) throw new InvalidOperationException("Restored operation capture unavailable.");
                (ObserveInitialOperation ?? throw new InvalidOperationException("Boarding observation unavailable."))(operation);
            },
            Quarantine = Quarantine
        }, report);
        _lifetime = hub.Subscribe("vgmodapi.dungeon-recovery-runtime", message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _initial.Clear(); _liveTransports.Clear(); _returns.Poll(); Pods.Clear(); Operations.Clear(); _captureFaults = new(); _podOwners = new(); _donors = new(); }
        });
    }
    internal bool BeginDonorUpdate(object actions, out DungeonMutationFence.Lease? abort)
    {
        abort = null;
        if (_native.Get(actions, "donorDispatched") is true) return true;
        if (_native.Get(actions, "donorTarget") is not Transform target || !target)
        { abort = State.BeginTransfer(); return abort != null; }
        return DonorReady(actions);
    }
    internal void DonorAborted(object actions)
    {
        try
        {
            if (!_donors.TryGetValue(actions, out var owner) || !ReferenceEquals(owner.Token, State.RestoreToken)) return;
            if (ReferenceEquals(_native.Get(owner.Ship, "donorActions"), actions)) return;
            if (!State.CompleteDonorAbort(owner.Operation, owner.ShipId, owner.Token)) State.RejectTransferSnapshot();
            _donors.Remove(actions);
        }
        catch (Exception error)
        {
            try { State.RejectTransferSnapshot(); } catch { }
            try { _report(error); } catch { }
        }
    }
    internal bool DonorReady(object actions)
    {
        if (!State.CanMutate) return false;
        if (_native.Get(actions, "donorTarget") is not Transform target || !target) return false;
        var boardable = target.GetComponent(_shipType.Assembly.GetType(BindingCatalog.Boardable, true)!);
        var operation = boardable == null ? null : ExistingOperation(boardable);
        return operation != null && OperationReady(operation);
    }
    internal bool QueueRestore(object target, out object? existing)
    {
        existing = ExistingOperation(target);
        var isLocation = target.GetType().FullName == BindingCatalog.BoardingLocation;
        var location = isLocation ? target : _native.Get(target, "data");
        if (location == null || !Operations.LocationMarker(location).HasValue) return false;
        if (existing == null) _initial.Queue(location, isLocation ? null : target);
        return true;
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
        if (!State.CanObserveSnapshots) return false;
        var location = _native.Get(operation, "location") ?? throw new InvalidOperationException("Missing operation location.");
        var id = Operations.OperationId(operation);
        if (!id.HasValue)
        {
            if (State.IsCheckpointing) return false;
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
        var walkReturn = previous.WalkReturn;
        if (walkReturn == null && phase == "Extraction" && _native.Get(location, "isShipBased") is false)
            walkReturn = new DungeonWalkReturnState((System.Collections.Generic.IReadOnlyDictionary<string, int>)_native.Call("walkManifest", operation, _native.Get(operation, "simulation")!)!);
        if (!State.TrackOperation(new(previous.Id, previous.LocationId, previous.ContentOccurrence, previous.AttackerShipId, previous.DungeonType,
            phase, outcome, previous.MissionProtection, previous.TerminalProgress, previous.Autonomous, _options.Capture(_native.Get(operation, "options")!), _world.CaptureDonors(_native.Get(operation, "boardableTarget"), (actions, ship, shipId) =>
            { _donors.Remove(actions); _donors.Add(actions, new(previous.Id, shipId, ship, State.RestoreToken)); }), (bool)_native.Get(operation, "resumeCrewWalking")!, walkReturn, previous.Retired || _native.Get(operation, "isComplete") is true))) return false;
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
            if (Pods.IdentityFor(data) is { } tracked) { _liveTransports.Track(tracked, pod); MarkLive(tracked); }
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
        if (_native.Get(location, "isShipBased") is false) _initial.Queue(location, null);
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
        if (State.Ready && _native.Manager is { } manager)
            State.Checkpoint(() =>
            {
                foreach (var operation in (System.Collections.IEnumerable)_native.Get(manager, "resumeOperations")!)
                    if (!ObserveOperation(operation)) throw new InvalidOperationException("Cannot checkpoint unresolved native operation.");
            });
        _returns.Checkpoint((_, _) => { });
        _liveTransports.Checkpoint((id, pod) =>
        {
            var data = _native.Get(pod, "resumePodData");
            if (_native.Get(data, "resumePodPhase")?.ToString() != "Returning") return;
            var previous = State.Get(id) ?? throw new InvalidOperationException("Missing live return state.");
            if (previous.ReturnDelivered) return;
            var transport = Pods.CaptureTransport(pod, previous.Transport!.PendingReinforcement,
                value => { var v = (Vector2)value; return (v.x, v.y); }, previous.Transport.DonorShipId);
            if (!State.RefreshTransportPose(id, transport.Pose)) throw new InvalidOperationException("Unable to checkpoint live return pose.");
        });
    }
    internal DungeonRefundHooks RefundHooks { get; }
    internal void Poll()
    {
        try { _initial.Poll(); _returns.Poll(); }
        catch (Exception error) { try { _report(error); } catch { } }
    }
    internal void BindInitialPod(DungeonReturnPodInstance instance, Guid id)
    {
        Pods.Loaded(instance.Data, id);
        if (Pods.Conflicted(id)) throw new InvalidOperationException("Duplicate initial pod identity.");
        _podOwners.Add(instance.Pod, instance.Operation); _liveTransports.Track(id, instance.Pod); MarkLive(id);
    }
    internal void MarkLive(Guid id) => _returns.MarkLive(id);
    public void Dispose() { _lifetime.Dispose(); _initial.Dispose(); _returns.Dispose(); _liveTransports.Clear(); Pods.Clear(); Operations.Clear(); State.Dispose(); }
}
