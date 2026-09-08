using System;
using System.Collections.Generic;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class BoardingCommandAdapter : IBoardingCommandBackend
{
    private readonly IBoardingCommandNativeBindings _native;
    private readonly BoardingObserver _observer;
    private readonly IBoardingEvents _events;
    private readonly Func<object, bool> _live;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, BoardingHandle> _owned = new();
    private readonly Stack<DebitScope> _debits = new();
    internal Func<object, bool>? ReinforcementAllowed;
    internal BoardingCommandAdapter(IBoardingCommandNativeBindings native, BoardingObserver observer, IBoardingEvents events, Func<object, bool> live)
    { _native = native; _observer = observer; _events = events; _live = live; }
    internal bool AllowAutonomous(object operation, bool enabled, BoardingCommandService commands)
    {
        if (!enabled) return true;
        var handle = _observer.CommandHandleForLocation(_native.Get(operation, "location"));
        return handle == null || !commands.HasControl(handle);
    }
    internal void HudCancel(object button, BoardingCommandService commands)
    {
        var boardable = _native.Get(button, "hudBoardable");
        var handle = _observer.CommandHandleForLocation(_native.Get(boardable, "data"));
        if (handle != null) commands.ManualTakeover(handle);
    }
    internal void ManualTakeover(object panel, BoardingCommandService commands)
    {
        var handle = _observer.CommandHandleForLocation(_native.Get(panel, "panelLocation"));
        if (handle != null) commands.ManualTakeover(handle);
    }
    internal Func<object, bool>? SimulationReady { get; set; }
    private bool Flag(object? obj, string key) => _native.Get(obj, key) is true;
    private Frame? Read(BoardingHandle target)
    {
        if (!_observer.TryResolveCommandTarget(target, out var location, out var component, out _)) return null;
        var player = _native.Player; var data = _native.Get(player, "playerShipData"); var ship = _native.Get(data, "ship"); var manager = _native.Manager;
        if (player == null || data == null || ship == null || manager == null || !_live(ship) || !_live(manager)) return null;
        var crewData = _native.Get(data, "crewData");
        if (crewData == null || _native.Get(crewData, "crew") is not Dictionary<string, int> roster) return null;
        var operation = _native.Call("commandGetOperation", manager, location!);
        var saved = _native.Get(_native.Get(location, "dungeonData"), "savedSimulation");
        var simulation = _native.Get(operation, "simulation") ?? saved;
        if (simulation != null && SimulationReady?.Invoke(simulation) == false) return null;
        var state = new BoardingCommandState
        {
            TargetAlive = component != null && _live(component),
            ShipAvailable = !Flag(ship, "destroyed") && (operation == null || ReferenceEquals(_native.Get(operation, "operationShip"), ship)),
            Travelling = (bool)_native.Call("travel", null)!, Enterable = Flag(location, "isShipBased") || Flag(location, "isEnterable"),
            LevelAllowed = Flag(location, "isShipBased") || !(bool)_native.Call("commandLevelGap", null, _native.Get(location, "level")!)!,
            HasOperation = operation != null, HasSavedSimulation = saved != null && !Flag(saved, "isComplete"),
            HasSimulation = simulation != null, SimulationComplete = Flag(simulation, "isComplete"),
            Retreating = Flag(simulation, "retreating"), Victory = Flag(simulation, "victoryAchieved"),
            AwaitingExtraction = Flag(simulation, "awaitingPlayerExtraction"), CanRequestExtraction = Flag(simulation, "canExtract"),
            CrewCapacity = (int)_native.Get(ship, "capacity")!, Crew = roster,
            Operation = _events.GetTarget(target)?.Operation
        };
        var faction = _native.Get(location, "faction");
        state.HasFactionConsequences = faction != null && !Flag(player, "transponder") &&
            !Flag(_native.Get(location, "shipData"), "noRepLoss") && (bool)_native.Call("commandFriendly", null, faction)!;
        foreach (var id in roster.Keys) if (_native.ValidCrew(id)) state.AllowedCrew.Add(id);
        var phase = _native.Get(operation, "phase")?.ToString();
        state.Phase = operation == null ? BoardingPhase.Available : Flag(operation, "isComplete") ? BoardingPhase.Resolved
            : phase == "Approach" ? (int)_native.Get(operation, "_podsInFlight")! > 0 ? BoardingPhase.AwaitingLanding : BoardingPhase.Approaching
            : phase == "Extraction" || state.AwaitingExtraction ? BoardingPhase.Extracting : BoardingPhase.Active;
        return new Frame(target, location!, component!, ship, crewData, manager, operation, simulation, state);
    }
    public BoardingCommandResult ValidateControl(BoardingHandle target)
    {
        var frame = Read(target);
        return BoardingCommandService.Result(frame == null || !frame.State.TargetAlive || !frame.State.ShipAvailable ? BoardingCommandStatus.TargetUnavailable
            : frame.State.Travelling ? BoardingCommandStatus.Travelling : BoardingCommandStatus.Admitted);
    }
    public void PauseAutonomous(BoardingHandle target)
    {
        var frame = Read(target);
        if (frame?.Operation != null) _native.Call("commandAutonomous", frame.Operation, false);
    }
    public BoardingCommandResult Execute(BoardingHandle target, BoardingCommandKind command, BoardingCrewManifest? crew, BoardingCommandOptions? options, bool allowFactionConsequences)
    {
        var frame = Read(target); if (frame == null) return BoardingCommandService.Result(BoardingCommandStatus.TargetUnavailable);
        var status = BoardingCommandValidation.Validate(frame.State, command, crew, options, allowFactionConsequences);
        if (status != BoardingCommandStatus.Admitted) return BoardingCommandService.Result(status);
        switch (command)
        {
            case BoardingCommandKind.Start:
                var nativeOptions = _native.CreateOptions(crew!, options!);
                object? started = null;
                var debitScope = new DebitScope(nativeOptions, frame.Ship);
                _debits.Push(debitScope);
                Exception? startError = null;
                try
                {
                    started = Flag(frame.Location, "isShipBased")
                        ? _native.Call("boardingStartShip", frame.Manager, frame.Ship, frame.Component, nativeOptions, false)
                        : _native.Call("boardingStartLocation", frame.Manager, frame.Ship, frame.Location, nativeOptions, false);
                    if (started != null) _owned.Add(started, target);
                }
                catch (Exception error) { startError = error; }
                var completedError = EndDebit(debitScope, startError);
                if (completedError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(completedError).Throw();
                if (started == null) return BoardingCommandService.Result(BoardingCommandStatus.NativeFailure);
                break;
            case BoardingCommandKind.Resume:
                if (frame.Operation != null) break;
                var resumed = Flag(frame.Location, "isShipBased")
                    ? _native.Call("boardingResumeShip", frame.Manager, frame.Component, false)
                    : _native.Call("boardingResumeLocation", frame.Manager, frame.Ship, frame.Location, false);
                if (resumed == null) return BoardingCommandService.Result(BoardingCommandStatus.NativeFailure);
                break;
            case BoardingCommandKind.Reinforce:
                if (ReinforcementAllowed != null && !ReinforcementAllowed(frame.Simulation!)) return BoardingCommandService.Result(BoardingCommandStatus.WrongPhase);
                var current = Read(target);
                if (current == null || !ReferenceEquals(current.Ship, frame.Ship) || !ReferenceEquals(current.Simulation, frame.Simulation)) return BoardingCommandService.Result(BoardingCommandStatus.StaleHandle);
                status = BoardingCrewTransfer.Transfer(frame.State.Crew, crew!, _native.ValidCrew, frame.State.CrewCapacity,
                    manifest => _native.Call("commandReinforce", frame.Operation, manifest), () => _native.Call("commandNotifyCrew", null));
                if (status != BoardingCommandStatus.Admitted) return BoardingCommandService.Result(status);
                break;
            case BoardingCommandKind.CancelApproach:
                _native.Call("commandAbandon", frame.Operation);
                break;
            case BoardingCommandKind.Retreat: _native.Call("commandRetreat", frame.Simulation, _native.OutcomeReason("VoluntaryRetreat")); break;
            case BoardingCommandKind.RequestExtraction: _native.Call("commandRequestExtraction", frame.Operation); break;
            case BoardingCommandKind.ConfirmExtraction: _native.Call("commandConfirmExtraction", frame.Operation); break;
            case BoardingCommandKind.SetOptions: _native.ApplyOptions(_native.Get(frame.Operation, "options")!, options!); break;
        }
        return new BoardingCommandResult(BoardingCommandStatus.Admitted, "Native command admitted; completion and crew settlement are observed separately.", _events.GetTarget(target)?.Operation);
    }
    internal bool RemoveAssigned(object operation, out Dictionary<string, int>? manifest)
    {
        manifest = null;
        if (_debits.Count == 0) return true;
        var scope = _debits.Peek();
        if (!ReferenceEquals(_native.Get(operation, "options"), scope.Options) || !ReferenceEquals(_native.Get(operation, "operationShip"), scope.Ship)) return true;
        if (scope.Consumed) { manifest = new Dictionary<string, int>(); return false; }
        var roster = (Dictionary<string, int>)_native.Get(_native.Get(_native.Get(_native.Player, "playerShipData"), "crewData"), "crew")!;
        if (!ReferenceEquals(_native.Get(_native.Get(_native.Player, "playerShipData"), "ship"), scope.Ship)) throw new InvalidOperationException("Boarding ship changed before crew debit.");
        var requested = new BoardingCrewManifest((Dictionary<string, int>)_native.Get(scope.Options, "assignedCrew")!);
        Dictionary<string, int>? removed = null;
        var status = BoardingCrewTransfer.Transfer(roster, requested, _native.ValidCrew, (int)_native.Get(scope.Ship, "capacity")!,
            crew => { removed = crew; scope.Consumed = true; }, () => scope.Notify = true);
        if (status != BoardingCommandStatus.Admitted) throw new InvalidOperationException("Boarding crew plan no longer valid: " + status);
        manifest = removed!; return false;
    }
    internal bool BeginWalk(object operation, out object? state)
    {
        state = null;
        if (!_owned.TryGetValue(operation, out var target)) return true;
        if (_events.SessionId != target.SessionId) return false;
        var frame = Read(target);
        var options = _native.Get(operation, "options")!;
        var crew = new BoardingCrewManifest((Dictionary<string, int>)_native.Get(options, "assignedCrew")!);
        if (frame == null || !frame.State.ShipAvailable || frame.State.Travelling || !frame.State.TargetAlive ||
            BoardingCrewTransfer.Validate(frame.State.Crew, crew, _native.ValidCrew, frame.State.CrewCapacity) != BoardingCommandStatus.Admitted)
        { _native.Call("commandAbandon", operation); return false; }
        var scope = new DebitScope(options, frame.Ship); _debits.Push(scope); state = scope; return true;
    }
    internal Exception? EndWalk(object? state, Exception? error) => state is DebitScope scope ? EndDebit(scope, error) : error;
    private Exception? EndDebit(DebitScope scope, Exception? error)
    {
        if (_debits.Count == 0 || !ReferenceEquals(_debits.Peek(), scope)) return new AggregateException("Boarding debit scope mismatch.", error ?? new InvalidOperationException());
        _debits.Pop();
        if (scope.Notify)
        {
            try { _native.Call("commandNotifyCrew", null); }
            catch (Exception notificationError) { return error == null ? notificationError : new AggregateException(error, notificationError); }
        }
        return error;
    }
    private sealed class DebitScope
    {
        internal readonly object Options, Ship;
        internal bool Consumed, Notify;
        internal DebitScope(object options, object ship) { Options = options; Ship = ship; }
    }
    private sealed class Frame
    {
        internal readonly BoardingHandle Target;
        internal readonly object Location, Component, Ship, CrewData, Manager;
        internal readonly object? Operation, Simulation;
        internal readonly BoardingCommandState State;
        internal Frame(BoardingHandle target, object location, object component, object ship, object crewData, object manager, object? operation, object? simulation, BoardingCommandState state)
        { Target = target; Location = location; Component = component; Ship = ship; CrewData = crewData; Manager = manager; Operation = operation; Simulation = simulation; State = state; }
    }
}
