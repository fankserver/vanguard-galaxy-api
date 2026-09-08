using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Inspected-build read-only integration. Reflection keeps unavailable native types out of plugin loading.</summary>
internal sealed class BoardingObserver : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly BoardingService _service;
    private readonly Action<Exception> _fault;
    private readonly Func<object, bool> _isLive;
    private readonly Func<object, string, object?> _read;
    private readonly Func<object, bool> _isDataInventory;
    private readonly Dictionary<object, Target> _targets = new();
    private readonly Dictionary<object, Operation> _operations = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object> _retiredOperations = new();
    private Guid? _session;
    private bool _stopped;
    private readonly Stack<RewardScope> _rewards = new();
    internal BoardingObserver(LifecycleHub hub, BoardingService service, Assembly assembly, Func<object, bool> isLive, Action<Exception> fault)
        : this(hub, service, Bind(assembly), isLive, fault, BindDataInventory(assembly)) { }
    internal BoardingObserver(LifecycleHub hub, BoardingService service, Func<object, string, object?> read, Func<object, bool> isLive, Action<Exception> fault, Func<object, bool>? isDataInventory = null)
    { _hub = hub; _service = service; _fault = fault; _isLive = isLive; _read = read; _isDataInventory = isDataInventory ?? (_ => false); }
    private static Func<object, bool> BindDataInventory(Assembly assembly)
    {
        var inventory = assembly.GetType("Source.Item.Inventory", true)!;
        var data = inventory.GetProperty("data", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (data?.PropertyType != inventory || data.GetMethod == null) throw new MissingMemberException("Inventory.data");
        var player = assembly.GetType(BindingCatalog.Player, true)!.GetField("current", BindingFlags.Public | BindingFlags.Static)!;
        return value => player.GetValue(null) != null && ReferenceEquals(data.GetValue(null), value);
    }
    private static Func<object, string, object?> Bind(Assembly assembly)
    {
        var members = new Dictionary<(Type, string), MemberInfo>();
        var game = new GameBindings(assembly);
        var queries = game.Resolve(BindingCatalog.BoardingQueries);
        // Resolve all snapshot members before any hooks are installed.
        foreach (var entry in BoardingMembers.Schema)
        {
            var type = assembly.GetType(entry.Type, true)!;
            MemberInfo? member = type.GetField(entry.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            member ??= type.GetProperty(entry.Name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (member is PropertyInfo property && (property.GetMethod == null || property.GetMethod.IsStatic || property.GetIndexParameters().Length != 0))
                throw new MissingMemberException(entry.Type, entry.Name);
            var actual = member is FieldInfo field ? field.FieldType : (member as PropertyInfo)?.PropertyType;
            if (actual == null || !NativeTypeName.Matches(actual, entry.ValueType)) throw new MissingMemberException(entry.Type, entry.Name);
            members.Add((type, entry.Name), member!);
        }
        object? Access(object obj, string name)
        {
            var type = obj.GetType();
            while (type != null)
            {
                if (members.TryGetValue((type, name), out var member))
                    return member is FieldInfo field ? field.GetValue(obj) : ((PropertyInfo)member).GetValue(obj);
                type = type.BaseType;
            }
            throw new MissingMemberException(obj.GetType().FullName, name);
        }
        return (obj, name) =>
        {
            if (name == "creditsBalance") return game.CurrentPlayer is object player ? Access(player, "credits") : null;
            if (name != "availability") return Access(obj, name);
            if (!(bool)Access(obj, "isShipBased")!)
            {
                if (!(bool)Access(obj, "isEnterable")!) return BoardingAvailability.IntegrityTooLow;
                if ((bool)queries["level"].Invoke(null, new[] { Access(obj, "level") })!) return BoardingAvailability.LevelTooHigh;
            }
            if ((bool)queries["travel"].Invoke(null, null)!) return BoardingAvailability.Travelling;
            if (!(bool)queries["crew"].Invoke(null, null)!) return BoardingAvailability.NoCrew;
            return BoardingAvailability.Available;
        };
    }
    private object? Read(object obj, string name) => _read(obj, name);
    private T Read<T>(object obj, string name) => (T)Read(obj, name)!;
    private int Pods(object obj) => ((ICollection)Read(obj, "_activePods")!).Count;
    private int PlayerPods(object obj) => ((IEnumerable)Read(obj, "_activePods")!).Cast<object>()
        .Count(pod => pod != null && _isLive(pod) && Read<bool>(pod, "isPlayerOwned"));
    private bool TargetLive(Target target) => target.Components.Count == 0 || target.Components.Any(_isLive);
    internal void Guard(Action action)
    {
        if (_stopped) return;
        try { _hub.CheckThread(); SyncSession(); if (_session.HasValue) action(); }
        catch (Exception error)
        {
            _stopped = true;
            try { Disable(); } catch { /* Foreign-thread faults reconcile at the next main-thread Poll. */ }
            try { _fault(error); } catch { }
        }
    }
    private void SyncSession()
    {
        if (_session == _service.SessionId) return;
        _targets.Clear(); _operations.Clear(); _retiredOperations.Clear(); _rewards.Clear(); _session = _service.SessionId;
    }
    internal void TargetReady(object native)
    {
        var location = Read(native, "data"); if (location == null) return;
        PublishTarget(EnsureTarget(location, native));
    }
    private Target EnsureTarget(object data, object? component = null)
    {
        _targets.TryGetValue(data, out var target);
        if (target != null && component != null && target.Components.Count > 0 && !target.Components.Contains(component) &&
            (!TargetLive(target) || Read<bool>(data, "isShipBased")))
        { RetireTarget(target); target = null; }
        if (target == null)
        { target = new Target(data, new BoardingHandle(_session!.Value, Guid.NewGuid())); _targets.Add(data, target); }
        if (component != null) target.Components.Add(component);
        return target;
    }
    internal void OperationReady(object? native, bool resumed)
    {
        if (native == null || _operations.ContainsKey(native) || _retiredOperations.TryGetValue(native, out _)) return;
        var target = EnsureTarget(Read(native, "location")!, Read(native, "boardableTarget"));
        var operation = new Operation(native, target, new BoardingHandle(_session!.Value, Guid.NewGuid()));
        if (resumed && Read(native, "simulation") is object saved)
        { operation.Victory = Read<bool>(saved, "victoryAchieved"); operation.Resolved = Read<bool>(saved, "isComplete"); }
        _operations.Add(native, operation); target.Operation = operation;
        Observe(operation, resumed ? BoardingEventKind.OperationResumed : BoardingEventKind.OperationStarted);
    }
    internal void BeforeOperation(object native)
    {
        if (_operations.TryGetValue(native, out var operation)) TrackPods(operation);
    }
    internal void OperationSignal(object native, string method, object? returnedPod = null)
    {
        if (!_operations.TryGetValue(native, out var operation)) return;
        if (method is "ReturnCrewToShip" or "HandlePodCrewReturned" or "ReturnAndDestroyDockedPod" or "ReturnDockedPodCrew" or "RecallAllPods" or "TriggerPodReturn") operation.ReturnObserved = true;
        if (method is "HandlePodCrewReturned" or "ReturnAndDestroyDockedPod" && returnedPod != null) operation.PendingPods.Remove(returnedPod);
        if (method is "ReturnDockedPodCrew" or "RecallAllPods" or "TriggerPodReturn")
        {
            var remaining = ((IEnumerable)Read(native, "_activePods")!).Cast<object>().ToArray();
            foreach (var pod in operation.PendingPods.Where(pair => pair.Value == "Docked" && !remaining.Contains(pair.Key)).Select(pair => pair.Key).ToArray())
                operation.PendingPods.Remove(pod);
        }
        Observe(operation);
    }
    private void TrackPods(Operation operation)
    {
        var pods = ((IEnumerable)Read(operation.Native, "_activePods")!).Cast<object>().ToArray();
        foreach (var pending in operation.PendingPods.Keys)
            if (!pods.Contains(pending) || !_isLive(pending)) operation.ReturnUnresolved = true;
        foreach (var pod in pods)
        {
            if (pod == null || !Read<bool>(pod, "isPlayerOwned")) continue;
            if (!_isLive(pod)) operation.ReturnUnresolved = true;
            operation.PendingPods[pod] = Read(pod, "state")!.ToString()!;
        }
    }
    internal object? BeginRewards(object native)
    {
        if (!_operations.TryGetValue(native, out var operation)) return null;
        var scope = new RewardScope(operation); _rewards.Push(scope); return scope;
    }
    internal void EndRewards(object? state)
    {
        if (state is not RewardScope scope || _rewards.Count == 0 || _rewards.Peek() != scope) return;
        _rewards.Pop();
        foreach (var delivery in scope.Deliveries) Observe(scope.Operation, BoardingEventKind.RewardsDelivered, delivery);
    }
    internal long? CreditBalance(object native) => _rewards.Count == 0 ? null : Read(native, "creditsBalance") as long?;
    internal void CreditsApplied(object native, long? before)
    {
        var after = CreditBalance(native);
        if (before.HasValue && after > before && after.Value - before.Value <= int.MaxValue)
            Delivered(new BoardingDelivery(BoardingDeliveryRoute.Credits, (int)(after.Value - before.Value)));
    }
    internal void InventoryApplied(object? result, int amount, object? inventory = null)
    {
        if (result != null && amount > 0) Delivered(new BoardingDelivery(inventory != null && _isDataInventory(inventory) ? BoardingDeliveryRoute.DataInventory : BoardingDeliveryRoute.Inventory, amount));
    }
    internal void WorldLootApplied(object poi, object data)
    {
        if (_rewards.Count == 0 || data.GetType().FullName != "Source.Data.Persistable.TractorableItemData") return;
        if (((IList)Read(poi, "persistables")!).Contains(data) && Read<int>(data, "itemAmount") > 0)
            Delivered(new BoardingDelivery(BoardingDeliveryRoute.WorldLoot, Read<int>(data, "itemAmount")));
    }
    private void Delivered(BoardingDelivery delivery)
    {
        if (_rewards.Count > 0) _rewards.Peek().Deliveries.Add(delivery);
    }
    internal void Captured(object native)
    {
        var data = Read(native, "data");
        if (data == null || Read(data, "shipData") == null || !_targets.TryGetValue(data, out var target) || target.Operation == null) return;
        Observe(target.Operation, BoardingEventKind.CaptureApplied);
    }
    private void Disable()
    {
        _service.Invalidate();
        _hub.SetCapability("boarding-observation", false, "Boarding observation stopped after an adapter fault.");
    }
    internal void Poll()
    {
        if (_stopped) { Disable(); return; }
        Guard(() =>
        {
            foreach (var target in _targets.Values.ToArray())
            {
                if (TargetLive(target)) PublishTarget(target);
                else RetireTarget(target);
            }
            foreach (var operation in _operations.Values.ToArray())
            {
                if ((!Read<bool>(operation.Native, "isComplete") && !operation.Target.Retired) || PlayerPods(operation.Native) != 0) continue;
                Observe(operation);
                if (operation.Target.Operation == operation) operation.Target.Operation = null;
                if (operation.Snapshot != null) _service.Observe(BoardingEventKind.OperationRetired, Snapshot(operation.Target), operation.Snapshot);
                _operations.Remove(operation.Native); _retiredOperations.Add(operation.Native, new object());
            }
        });
    }
    private void RetireTarget(Target target)
    {
        if (target.Retired) return;
        target.Retired = true;
        var snapshot = Snapshot(target); target.Snapshot = snapshot;
        _service.Observe(BoardingEventKind.Retired, snapshot, target.Operation?.Snapshot);
        if (_targets.TryGetValue(target.Data, out var current) && current == target) _targets.Remove(target.Data);
    }
    private void PublishTarget(Target target)
    {
        var previous = target.Snapshot; var snapshot = Snapshot(target);
        if (previous == null || previous.Availability != snapshot.Availability || !Equals(previous.Operation, snapshot.Operation))
        {
            target.Snapshot = snapshot;
            _service.Observe(previous == null ? BoardingEventKind.TargetAvailable : BoardingEventKind.TargetChanged, snapshot);
        }
    }
    private BoardingTargetSnapshot Snapshot(Target target)
    {
        var data = target.Data; var dead = target.Retired || !TargetLive(target);
        var availability = dead ? BoardingAvailability.TargetUnavailable : target.Operation != null && !Read<bool>(target.Operation.Native, "isComplete")
            ? BoardingAvailability.OperationActive : Read<BoardingAvailability>(data, "availability");
        var template = Read(data, "shipTemplate") as string; var faction = Read(data, "faction");
        return new BoardingTargetSnapshot(target.Handle, ++target.Revision, Read<bool>(data, "isShipBased") ? BoardingEncounterKind.Ship : BoardingEncounterKind.Installation,
            string.IsNullOrEmpty(template) ? Read(data, "dungeonType")!.ToString()! : template!, faction == null ? null : Read(faction, "identifier") as string,
            template, availability, target.Operation?.Handle);
    }
    private void Observe(Operation operation, BoardingEventKind? explicitKind = null, BoardingDelivery? delivery = null)
    {
        TrackPods(operation);
        var native = operation.Native; var sim = Read(native, "simulation"); var pods = Pods(native);
        var playerPods = PlayerPods(native);
        var phase = Read<bool>(native, "isComplete") ? playerPods > 0 ? BoardingPhase.ReturningCrew : operation.ReturnObserved && !operation.ReturnUnresolved && operation.PendingPods.Count == 0 ? BoardingPhase.Settled : BoardingPhase.Resolved
            : Read(native, "phase")!.ToString() == "Approach" ? Read<int>(native, "_podsInFlight") > 0 ? BoardingPhase.AwaitingLanding : BoardingPhase.Approaching
            : Read(native, "phase")!.ToString() == "Extraction" || (sim != null && Read<bool>(sim, "awaitingPlayerExtraction")) ? BoardingPhase.Extracting : BoardingPhase.Active;
        var rooms = sim == null ? Array.Empty<BoardingCompartmentSnapshot>() : ((IEnumerable)Read(sim, "compartments")!).Cast<object>()
            .Where(room => Read(room, "state")!.ToString() != "Unknown")
            .Select(room => new BoardingCompartmentSnapshot(Read<int>(room, "index"), Read(room, "type")!.ToString()!, Read(room, "state")!.ToString()!,
                Read<bool>(room, "isLocked"), Read<bool>(room, "isDestroyed"), CountCrew(sim, "friendlyUnits", Read<int>(room, "index")), CountCrew(sim, "hostileUnits", Read<int>(room, "index")))).ToArray();
        var options = Read(native, "options")!;
        var snapshot = new BoardingOperationSnapshot(operation.Handle, operation.Target.Handle, ++operation.Revision, phase,
            Read<bool>(native, "isAutonomous"), Read<bool>(options, "autoMove"), sim == null ? null : Read<float>(sim, "structureIntegrity"), sim == null ? null : Read<float>(sim, "maxStructureIntegrity"),
            sim == null ? null : Read(sim, "outcome")!.ToString(), (IEnumerable<KeyValuePair<string, int>>)Read(options, "assignedCrew")!, rooms, pods);
        var fingerprint = phase + "/" + snapshot.Autonomous + "/" + snapshot.AutoMove + "/" + snapshot.Integrity + "/" + snapshot.MaximumIntegrity + "/" + string.Join(";", snapshot.AssignedCrew.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + ":" + pair.Value)) + "/" + snapshot.Outcome + "/" + pods + "/" +
            string.Join(";", rooms.Select(room => room.Index + ":" + room.State + ":" + room.Locked + ":" + room.Destroyed + ":" + room.FriendlyCrew + ":" + room.HostileCrew));
        if (explicitKind == null && fingerprint == operation.Fingerprint) return;
        var old = operation.Snapshot; operation.Fingerprint = fingerprint; operation.Snapshot = snapshot;
        var target = Snapshot(operation.Target); operation.Target.Snapshot = target;
        var kind = explicitKind ?? (old?.Phase != phase ? BoardingEventKind.PhaseChanged : BoardingEventKind.TacticalChanged);
        _service.Observe(kind, target, snapshot, delivery);
        if (sim != null && Read<bool>(sim, "victoryAchieved") && !operation.Victory)
        { operation.Victory = true; _service.Observe(BoardingEventKind.VictorySecured, target, snapshot); }
        if (sim != null && Read<bool>(sim, "isComplete") && !operation.Resolved)
        { operation.Resolved = true; _service.Observe(BoardingEventKind.SimulationResolved, target, snapshot); }
        if (phase == BoardingPhase.Settled && !operation.Settled)
        { operation.Settled = true; _service.Observe(BoardingEventKind.CrewReturnSettled, target, snapshot); }
    }
    private int CountCrew(object sim, string side, int index) => ((IEnumerable)Read(sim, side)!).Cast<object>()
        .Count(unit => Read<int>(unit, "compartmentIndex") == index && Read<int>(unit, "hp") > 0 && Read(unit, "state")!.ToString() is not ("Killed" or "Surrendered" or "Captured"));
    public void Dispose() { _stopped = true; _targets.Clear(); _operations.Clear(); _service.Dispose(); }
    internal BoardingHandle? CommandHandleForOperation(object native)
    {
        _hub.CheckThread();
        return _operations.TryGetValue(native, out var operation) && _service.GetOperation(operation.Handle) != null ? operation.Handle : null;
    }
    internal object? ResolveCommandOperation(BoardingHandle handle)
    {
        _hub.CheckThread();
        if (_service.GetOperation(handle) == null) return null;
        return _operations.Values.FirstOrDefault(operation => operation.Handle.Equals(handle))?.Native;
    }

    internal BoardingHandle? CommandHandleForLocation(object? location)
    {
        _hub.CheckThread();
        return location != null && _targets.TryGetValue(location, out var target) && !target.Retired && _service.GetTarget(target.Handle) != null ? target.Handle : null;
    }

    /// <summary>Resolve only the current observed generation; never revive a retired target for commands.</summary>
    internal bool TryResolveCommandTarget(BoardingHandle handle, out object? location, out object? component, out object? operation)
    {
        _hub.CheckThread(); location = null; component = null; operation = null;
        if (_service.GetTarget(handle) == null) return false;
        var target = _targets.Values.FirstOrDefault(value => !value.Retired && value.Handle.Equals(handle));
        if (target == null) return false;
        component = target.Components.FirstOrDefault(_isLive);
        if (component == null) return false;
        location = target.Data;
        if (target.Operation != null && _operations.ContainsKey(target.Operation.Native)) operation = target.Operation.Native;
        return true;
    }

    private sealed class RewardScope
    {
        internal readonly Operation Operation;
        internal readonly List<BoardingDelivery> Deliveries = new();
        internal RewardScope(Operation operation) { Operation = operation; }
    }
    private sealed class Target
    {
        internal readonly object Data;
        internal readonly BoardingHandle Handle;
        internal readonly HashSet<object> Components = new();
        internal bool Retired;
        internal Operation? Operation;
        internal BoardingTargetSnapshot? Snapshot;
        internal long Revision;
        internal Target(object data, BoardingHandle handle) { Data = data; Handle = handle; }
    }
    private sealed class Operation
    {
        internal readonly object Native;
        internal readonly Target Target;
        internal readonly BoardingHandle Handle;
        internal BoardingOperationSnapshot? Snapshot;
        internal long Revision;
        internal string? Fingerprint;
        internal bool Victory, Resolved, Settled, ReturnObserved, ReturnUnresolved;
        internal readonly Dictionary<object, string> PendingPods = new();
        internal Operation(object native, Target target, BoardingHandle handle) { Native = native; Target = target; Handle = handle; }
    }
}
