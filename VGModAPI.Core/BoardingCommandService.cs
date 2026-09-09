using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal enum BoardingCommandKind { Start, Resume, Reinforce, CancelApproach, Retreat, RequestExtraction, ConfirmExtraction, SetOptions }

/// <summary>The inspected adapter validates current native state immediately before each mutation.</summary>
internal interface IBoardingCommandBackend
{
    BoardingCommandResult ValidateControl(BoardingHandle target);
    void PauseAutonomous(BoardingHandle target);
    BoardingCommandResult Execute(BoardingHandle target, BoardingCommandKind command, BoardingCrewManifest? crew,
        BoardingCommandOptions? options, bool allowFactionConsequences);
}

internal sealed class BoardingCommandService : IDungeonCommandService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly IDungeonOperationService? _events;
    private readonly IServiceStatus _status;
    private readonly IBoardingCommandBackend? _backend;
    private readonly Func<bool> _rulesEvaluating;
    private readonly Dictionary<BoardingHandle, Controller> _controllers = new();
    private bool _busy, _disposed;
    private int _serializationDepth;
    internal void BeginSerialization() { _hub.CheckThread(); _serializationDepth++; }
    internal void EndSerialization() { _hub.CheckThread(); if (_serializationDepth > 0) _serializationDepth--; }
    internal BoardingCommandService(LifecycleHub hub, IDungeonOperationService? events, IBoardingCommandBackend? backend, Func<bool> rulesEvaluating)
    {
        _hub = hub; _events = events; _backend = backend; _rulesEvaluating = rulesEvaluating;
        _status = hub.Services.Get("boarding-commands");
        if ((events == null || backend == null) && Availability.IsAvailable) hub.SetCapability("boarding-commands", false, "Command bindings unavailable.");
    }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    private BoardingCommandResult? Gate(BoardingHandle target)
    {
        _hub.CheckThread();
        if (_disposed || _events == null || _backend == null || !Availability.IsAvailable) return Result(BoardingCommandStatus.IntegrationUnavailable);
        if (_busy || _serializationDepth > 0 || _hub.IsDispatchingCallbacks || _events.IsDispatchingCallbacks || _rulesEvaluating()) return Result(BoardingCommandStatus.Busy);
        var session = _hub.CurrentSession;
        if (session == null || session.Phase is not (SessionPhase.PlayerReady or SessionPhase.GameplayInitialized)) return Result(BoardingCommandStatus.SessionUnavailable);
        if (target.SessionId != session.Id || _events.GetTarget(target) == null) return Result(BoardingCommandStatus.StaleHandle);
        return null;
    }
    public BoardingCommandResult AcquireControl(string pluginId, BoardingHandle target, out IBoardingController? controller)
    {
        _hub.CheckThread(); controller = null;
        if (string.IsNullOrWhiteSpace(pluginId)) throw new ArgumentException("Plugin ID required.", nameof(pluginId));
        if (target == null) throw new ArgumentNullException(nameof(target));
        var rejected = Gate(target); if (rejected != null) return rejected;
        foreach (var stale in _controllers.Values.Where(c => !Current(c)).ToArray()) Remove(stale);
        if (_controllers.ContainsKey(target)) return Result(BoardingCommandStatus.ControlConflict);
        _busy = true;
        try
        {
            var validation = _backend!.ValidateControl(target); if (!validation.Admitted) return validation;
            if (_disposed || !Availability.IsAvailable) return Result(BoardingCommandStatus.IntegrationUnavailable);
            if (_hub.CurrentSession?.Id != target.SessionId || _events?.GetTarget(target) == null) return Result(BoardingCommandStatus.StaleHandle);
            _backend.PauseAutonomous(target);
            if (_disposed || !Availability.IsAvailable) return Result(BoardingCommandStatus.Uncertain);
            // Native callbacks may replace the session or retire the target during arbitration.
            if (_disposed || _hub.CurrentSession?.Id != target.SessionId || _events?.GetTarget(target) == null) return Result(BoardingCommandStatus.StaleHandle);
            var created = new Controller(this, pluginId, target); _controllers.Add(target, created); controller = created;
            return Result(BoardingCommandStatus.Admitted);
        }
        finally { _busy = false; }
    }
    private bool Current(Controller controller) => !controller.Disposed && !_disposed && Availability.IsAvailable &&
        _hub.CurrentSession?.Id == controller.Target.SessionId && _events?.GetTarget(controller.Target) != null &&
        _controllers.TryGetValue(controller.Target, out var current) && ReferenceEquals(current, controller);
    private BoardingCommandResult Execute(Controller controller, BoardingCommandKind command, BoardingCrewManifest? crew = null,
        BoardingCommandOptions? options = null, bool allowFactionConsequences = false)
    {
        var rejected = Gate(controller.Target); if (rejected != null) return rejected;
        if (!Current(controller)) return Result(BoardingCommandStatus.ControlConflict);
        _busy = true;
        try { return RevalidateOutcome(controller.Target, _backend!.Execute(controller.Target, command, crew, options, allowFactionConsequences)); }
        finally { _busy = false; }
    }
    internal BoardingCommandResult ExecuteControlled(IBoardingController controller, Func<BoardingHandle, BoardingCommandResult> action)
    {
        _hub.CheckThread();
        if (controller is not Controller owned || !owned.OwnedBy(this)) return Result(BoardingCommandStatus.ControlConflict);
        var rejected = Gate(owned.Target); if (rejected != null) return rejected;
        if (!Current(owned)) return Result(BoardingCommandStatus.ControlConflict);
        _busy = true;
        try { return RevalidateOutcome(owned.Target, action(owned.Target)); }
        finally { _busy = false; }
    }
    private BoardingCommandResult RevalidateOutcome(BoardingHandle target, BoardingCommandResult result) =>
        !_disposed && Availability.IsAvailable && _hub.CurrentSession?.Id == target.SessionId ? result :
            new BoardingCommandResult(BoardingCommandStatus.Uncertain, "Context changed during execution; effects may have occurred. Do not retry blindly.");
    /// <summary>Manual native UI action wins over a mod controller. Called before the manual mutation, not observer delivery.</summary>
    internal bool HasControl(BoardingHandle target)
    {
        _hub.CheckThread(); return _controllers.TryGetValue(target, out var controller) && Current(controller);
    }
    internal void ManualTakeover(BoardingHandle target)
    {
        _hub.CheckThread();
        if (_controllers.TryGetValue(target, out var controller)) Remove(controller);
    }
    private void Remove(Controller controller)
    {
        _hub.CheckThread(); controller.Disposed = true;
        if (_controllers.TryGetValue(controller.Target, out var current) && ReferenceEquals(current, controller)) _controllers.Remove(controller.Target);
        // Never restore an old autonomous setting over a manual takeover or a later controller's choice.
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (Availability.IsAvailable) _hub.SetCapability("boarding-commands", false, "Command service stopped.", ServiceUnavailableReason.ApiStopped);
        foreach (var controller in _controllers.Values.ToArray()) Remove(controller);
    }
    internal static BoardingCommandResult Result(BoardingCommandStatus status) => new(status, status.ToString());
    private sealed class Controller : IBoardingController
    {
        private readonly BoardingCommandService _owner;
        internal readonly string PluginId;
        internal bool Disposed;
        public BoardingHandle Target { get; }
        internal bool OwnedBy(BoardingCommandService owner) => ReferenceEquals(_owner, owner);
        public bool IsActive { get { _owner._hub.CheckThread(); return _owner.Current(this); } }
        internal Controller(BoardingCommandService owner, string pluginId, BoardingHandle target) { _owner = owner; PluginId = pluginId; Target = target; }
        public BoardingCommandResult Start(BoardingCrewManifest crew, BoardingCommandOptions options, bool allowFactionConsequences = false)
            => _owner.Execute(this, BoardingCommandKind.Start, crew ?? throw new ArgumentNullException(nameof(crew)), options ?? throw new ArgumentNullException(nameof(options)), allowFactionConsequences);
        public BoardingCommandResult Resume() => _owner.Execute(this, BoardingCommandKind.Resume);
        public BoardingCommandResult Reinforce(BoardingCrewManifest crew) => _owner.Execute(this, BoardingCommandKind.Reinforce, crew ?? throw new ArgumentNullException(nameof(crew)));
        public BoardingCommandResult CancelApproach() => _owner.Execute(this, BoardingCommandKind.CancelApproach);
        public BoardingCommandResult Retreat() => _owner.Execute(this, BoardingCommandKind.Retreat);
        public BoardingCommandResult RequestExtraction() => _owner.Execute(this, BoardingCommandKind.RequestExtraction);
        public BoardingCommandResult ConfirmExtraction() => _owner.Execute(this, BoardingCommandKind.ConfirmExtraction);
        public BoardingCommandResult SetOptions(BoardingCommandOptions options) => _owner.Execute(this, BoardingCommandKind.SetOptions, options: options ?? throw new ArgumentNullException(nameof(options)));
        public void Dispose() => _owner.Remove(this);
    }
}
