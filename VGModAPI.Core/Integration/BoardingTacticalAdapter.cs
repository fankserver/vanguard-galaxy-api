using System;
using System.Collections;
using System.Linq;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class BoardingTacticalAdapter : IDungeonTacticalService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IBoardingTacticalNativeBindings? _bindings;
    private IBoardingTacticalNativeBindings _native => _bindings ?? throw new InvalidOperationException("Tactical bindings unavailable.");
    private readonly IServiceStatus _status;
    private bool _disposed;
    private readonly BoardingObserver? _observer;
    private readonly IDungeonOperationService? _events;
    private readonly BoardingCommandService? _commands;
    internal BoardingTacticalAdapter(LifecycleHub hub, GameBindings game, BoardingObserver observer, IDungeonOperationService events, BoardingCommandService commands)
        : this(hub, new BoardingCommandNativeBindings(game, BoardingTacticalBindings.Actions.Concat(BoardingTacticalBindings.Queries).ToArray(), BoardingTacticalBindings.Members), observer, events, commands) { }
    internal BoardingTacticalAdapter(LifecycleHub hub, IBoardingTacticalNativeBindings? native, BoardingObserver? observer, IDungeonOperationService? events, BoardingCommandService? commands)
    {
        _hub = hub; _bindings = native; _observer = observer; _events = events; _commands = commands;
        _status = hub.Services.Get("boarding-tactics");
        if (native == null && Availability.IsAvailable) hub.SetCapability("boarding-tactics", false, "Tactical bindings unavailable.");
    }
    internal BoardingTacticalAdapter(LifecycleHub hub, BoardingCommandService commands)
        : this(hub, (IBoardingTacticalNativeBindings?)null, null, null, commands) { }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("boarding-tactics", false, "Tactical service stopped.", ServiceUnavailableReason.ApiStopped);
    }
    private bool Flag(object? obj, string key) => _native.Get(obj, key) is true;
    private object? Simulation(BoardingHandle target)
    {
        if (_observer == null || !_observer.TryResolveCommandTarget(target, out _, out _, out var operation)) return null;
        return _native.Get(operation, "simulation");
    }
    public BoardingTacticalSnapshot? GetSnapshot(BoardingHandle operation)
    {
        _hub.CheckThread();
        if (operation == null) throw new ArgumentNullException(nameof(operation));
        if (_disposed || !Availability.IsAvailable || _bindings == null || _events == null || _observer == null) return null;
        var observed = _events.GetOperation(operation); if (observed == null) return null;
        var nativeOperation = _observer.ResolveCommandOperation(operation);
        var simulation = _native.Get(nativeOperation, "simulation"); if (simulation == null) return null;
        var visible = observed.Compartments.ToList();
        var rooms = (IList)_native.Get(simulation, "compartments")!;
        for (var index = 0; index < rooms.Count; index++)
        {
            if (visible.Any(room => room.Index == index)) continue;
            var state = Read(simulation, new BoardingTacticalRequest(BoardingTacticalAction.Move, index), out _);
            if (state.Discovered) visible.Add(new BoardingCompartmentSnapshot(index, "Unknown", "Unknown", state.Locked, state.Destroyed, 0, 0));
        }
        var snapshot = new BoardingTacticalSnapshot(operation, visible.OrderBy(room => room.Index), (int)_native.Get(simulation, "grenades")!,
            (float)_native.Call("tacticalCooldown", simulation)!, Flag(simulation, "canExtract"), Flag(simulation, "awaitingPlayerExtraction"));
        return !_disposed && Availability.IsAvailable && _hub.CurrentSession?.Id == operation.SessionId ? snapshot : null;
    }
    public BoardingCommandResult Execute(IBoardingController controller, BoardingTacticalRequest request)
    {
        _hub.CheckThread();
        if (controller == null) throw new ArgumentNullException(nameof(controller));
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (_disposed || !Availability.IsAvailable || _bindings == null || _commands == null || _observer == null) return BoardingCommandService.Result(BoardingCommandStatus.IntegrationUnavailable);
        var outcome = _commands.ExecuteControlled(controller, target =>
        {
            var simulation = Simulation(target);
            if (simulation == null) return BoardingCommandService.Result(BoardingCommandStatus.WrongPhase);
            var state = Read(simulation, request, out var specialist);
            var status = BoardingTacticalValidation.Validate(state, request);
            if (status != BoardingCommandStatus.Admitted) return BoardingCommandService.Result(status);
            if (_disposed || !Availability.IsAvailable) return BoardingCommandService.Result(BoardingCommandStatus.IntegrationUnavailable);
            if (_hub.CurrentSession?.Id != target.SessionId) return BoardingCommandService.Result(BoardingCommandStatus.StaleHandle);
            var room = request.Compartment ?? -1; object? result = null;
            switch (request.Action)
            {
                case BoardingTacticalAction.Move: _native.Call("tacticalMove", simulation, room, _native.EnumArgument("tacticalMove", 1, request.Filter.ToString()), request.Count); break;
                case BoardingTacticalAction.ClearMovement: _native.Call("tacticalClear", simulation, room); break;
                case BoardingTacticalAction.RetreatFromCompartment: _native.Call("tacticalRetreat", simulation, room); break;
                case BoardingTacticalAction.SetPriority: _native.Set(_native.Get(simulation, "simulationOptions")!, "priority", room); break;
                case BoardingTacticalAction.ClearPriority: _native.Set(_native.Get(simulation, "simulationOptions")!, "priority", null); break;
                case BoardingTacticalAction.Unlock: result = _native.Call("tacticalUnlock", simulation, room, specialist!); break;
                case BoardingTacticalAction.ToggleBarricade: result = _native.Call("tacticalBarricade", simulation, room); break;
                case BoardingTacticalAction.ThrowGrenade: result = _native.Call("tacticalGrenade", simulation, room); break;
                case BoardingTacticalAction.AcceptBuyout: result = _native.Call("tacticalBuyout", simulation); break;
                case BoardingTacticalAction.DeclineBuyout: _native.Call("tacticalDecline", simulation); break;
                case BoardingTacticalAction.RequestExtraction: _native.Call("tacticalRequestExtraction", simulation); break;
                case BoardingTacticalAction.ConfirmExtraction: _native.Call("tacticalConfirmExtraction", simulation); break;
            }
            return BoardingCommandService.Result(result is false ? BoardingCommandStatus.WrongPhase : BoardingCommandStatus.Admitted);
        });
        return !_disposed && Availability.IsAvailable ? outcome :
            new BoardingCommandResult(BoardingCommandStatus.Uncertain, "Tactical context changed; effects may have occurred. Do not retry blindly.");
    }
    internal bool ValidateNative(object simulation, string method, object[] arguments)
    {
        _hub.CheckThread();
        if (_hub.CurrentSession?.Phase is not (SessionPhase.PlayerReady or SessionPhase.GameplayInitialized)) return true;
        if (method == "MoveCrewTo") return ValidateDirectMovement(simulation, (IList)arguments[0], (int)arguments[1]);
        var action = method switch
        {
            "IssueMovementOrder" => BoardingTacticalAction.Move,
            "ClearPlayerMovementOrders" => BoardingTacticalAction.ClearMovement,
            "RetreatFromCompartment" => BoardingTacticalAction.RetreatFromCompartment,
            "TryUnlockCompartment" => BoardingTacticalAction.Unlock,
            "ToggleBarricade" => BoardingTacticalAction.ToggleBarricade,
            "ThrowGrenade" => BoardingTacticalAction.ThrowGrenade,
            "AcceptBuyOut" => BoardingTacticalAction.AcceptBuyout,
            "DeclineBuyOut" => BoardingTacticalAction.DeclineBuyout,
            "RequestExtraction" => BoardingTacticalAction.RequestExtraction,
            "ConfirmExtraction" => BoardingTacticalAction.ConfirmExtraction,
            _ => throw new ArgumentException("Unmapped tactical boundary.", nameof(method))
        };
        int? room = arguments.Length > 0 ? (int)arguments[0] : null;
        if (room < 0) return false;
        var filter = BoardingMovementFilter.Any; var count = 1;
        if (action == BoardingTacticalAction.Move)
        {
            if (!Enum.TryParse(arguments[1].ToString(), out filter) || !Enum.IsDefined(typeof(BoardingMovementFilter), filter)) return false;
            count = (int)arguments[2]; if (count < 1) return false;
        }
        var request = new BoardingTacticalRequest(action, room, filter, count, allowFriendlyDamage: true);
        var state = Read(simulation, request, out var specialist);
        if (action == BoardingTacticalAction.Unlock)
        {
            // A different eligible specialist does not authorize this particular native argument.
            if (!ReferenceEquals(specialist, arguments[1])) state.AdjacentSpecialist = IsUnlocker(simulation, arguments[1], room!.Value);
        }
        return BoardingTacticalValidation.Validate(state, request) == BoardingCommandStatus.Admitted;
    }
    private bool ValidateDirectMovement(object simulation, IList units, int target)
    {
        if (target < 0 || units.Count == 0) return false;
        var request = new BoardingTacticalRequest(BoardingTacticalAction.Move, target, count: 1);
        var state = Read(simulation, request, out _);
        // Native direct movement intentionally takes the subset that fits; it is not a queued count request.
        if (!state.Active || !state.HasCompartment || !state.Discovered || state.Destroyed || state.Locked) return false;
        var all = (IList)_native.Get(simulation, "friendlyUnits")!;
        var rooms = (IList)_native.Get(simulation, "compartments")!;
        var seen = new System.Collections.Generic.HashSet<object>(); var specialist = false;
        foreach (var unit in units)
        {
            if (unit == null || !seen.Add(unit) || !all.Contains(unit) || !Flag(unit, "friendly") || Flag(unit, "transit") || !(bool)_native.Call("tacticalAlive", unit)!) return false;
            var origin = (int)_native.Get(unit, "compartmentIndex")!;
            if (origin < 0 || origin >= rooms.Count || !((IList)_native.Get(rooms[origin], "adjacent")!).Contains(target)) return false;
            specialist |= (bool)_native.Call("tacticalSpecialist", simulation, unit)!;
        }
        return !state.Sealed || specialist;
    }
    private bool IsUnlocker(object simulation, object unit, int target)
    {
        var units = (IList)_native.Get(simulation, "friendlyUnits")!;
        if (!units.Contains(unit) || !Flag(unit, "friendly") || Flag(unit, "transit") || !(bool)_native.Call("tacticalAlive", unit)!) return false;
        var rooms = (IList)_native.Get(simulation, "compartments")!; var origin = (int)_native.Get(unit, "compartmentIndex")!;
        if (origin < 0 || origin >= rooms.Count || !((IList)_native.Get(rooms[origin], "adjacent")!).Contains(target)) return false;
        var assigned = (int)_native.Get(unit, "directiveTarget")!;
        return (assigned == -1 || assigned == target) && (bool)_native.Call("tacticalSpecialist", simulation, unit)!;
    }
    internal BoardingTacticalState Read(object simulation, BoardingTacticalRequest request, out object? specialist)
    {
        specialist = null;
        var state = new BoardingTacticalState
        {
            Active = !Flag(simulation, "isComplete") && !Flag(simulation, "retreating"),
            GrenadeCharges = (int)_native.Get(simulation, "grenades")!, GrenadeCooldown = (float)_native.Call("tacticalCooldown", simulation)!,
            BuyoutPending = Flag(simulation, "buyoutPending"), BuyoutCost = (int)_native.Get(simulation, "buyoutCost")!,
            Credits = (long?)_native.Get(_native.Player, "credits") ?? 0,
            Victory = Flag(simulation, "victoryAchieved"), AwaitingExtraction = Flag(simulation, "awaitingPlayerExtraction"), CanRequestExtraction = Flag(simulation, "canExtract")
        };
        if (request.Action == BoardingTacticalAction.AcceptBuyout) state.BuyoutCandidate = _native.Call("tacticalCandidate", simulation) != null;
        if (!request.Compartment.HasValue) return state;
        var rooms = (IList)_native.Get(simulation, "compartments")!; var index = request.Compartment.Value;
        if (index < 0 || index >= rooms.Count) return state;
        var room = rooms[index]!; state.HasCompartment = true;
        state.Discovered = _native.Get(room, "state")!.ToString() != "Unknown";
        state.Destroyed = Flag(room, "isDestroyed"); state.Locked = Flag(room, "isLocked"); state.Sealed = Flag(room, "sealed");
        foreach (var unit in (IList)_native.Get(simulation, "friendlyUnits")!)
        {
            if (unit == null || !(bool)_native.Call("tacticalAlive", unit)! || !Flag(unit, "friendly") || Flag(unit, "transit")) continue;
            var origin = (int)_native.Get(unit, "compartmentIndex")!;
            if (origin == index) state.FriendlyCrew++;
            if (origin < 0 || origin >= rooms.Count || !((IList)_native.Get(rooms[origin], "adjacent")!).Contains(index)) continue;
            if (request.Action is BoardingTacticalAction.Move or BoardingTacticalAction.SetPriority or BoardingTacticalAction.Unlock) state.Discovered = true;
            if (!(bool)_native.Call("tacticalSpecialist", simulation, unit)!) continue;
            var assigned = (int)_native.Get(unit, "directiveTarget")!;
            if (assigned != -1 && assigned != index) continue;
            specialist ??= unit;
        }
        state.AdjacentSpecialist = specialist != null;
        if (request.Action == BoardingTacticalAction.Move)
        {
            state.Capacity = (int)_native.Call("tacticalCapacity", simulation, index)!;
            state.EligibleCrew = (int)_native.Call("tacticalEligible", simulation, index, _native.EnumArgument("tacticalEligible", 1, state.Locked ? "Specialist" : request.Filter.ToString()))!;
        }
        if (request.Action == BoardingTacticalAction.Unlock) state.UnlockInProgress = (float)_native.Call("tacticalUnlockTime", simulation, index)! > 0;
        if (request.Action == BoardingTacticalAction.ToggleBarricade)
        { state.CanBarricade = (bool)_native.Call("tacticalCanBarricade", simulation, index)!; state.BarricadeHeld = (bool)_native.Call("tacticalBarricadeHeld", simulation, index)!; }
        if (request.Action == BoardingTacticalAction.ThrowGrenade) state.CanGrenade = (bool)_native.Call("tacticalCanGrenade", simulation, index)!;
        return state;
    }
}
