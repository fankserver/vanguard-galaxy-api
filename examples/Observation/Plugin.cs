using System;
using BepInEx;
using VGModAPI;

namespace Observation;

/// <summary>
/// Sample/test mod demonstrating how a mod **reads** the running game without changing it: session
/// lifecycle, mission transitions and travel transitions, each observed once, reported honestly and
/// unsubscribed cleanly.
///
/// Every callback here observes only. None of them blocks, mutates an in-progress load/save, or
/// retains a vanilla reference. That restriction belongs to these low-level observational hooks — it
/// is NOT the model for domain events intended for gameplay reactions (see the StoryMissions and
/// CargoRecovery examples, whose events deliberately support normal follow-up actions).
///
/// The Unity-free injected consumers in `Consumers/` show the same discipline as plain .NET classes.
/// </summary>
[BepInPlugin(Id, "Observation example", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
[BepInProcess("VanguardGalaxy.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private const string Id = "vgmodapi.example.observation";

    private ILifecycleService? _lifecycle;
    private IMissionService? _missions;
    private ITravelService? _travel;
    private IHudRegistration? _hud;

    private int _lifecycleEvents;
    private int _missionTransitions;
    private int _routesCompleted;
    private string _lastLifecycle = "-";
    private string _lastMission = "-";
    private string _lastTravel = "-";

    private void Start()
    {
        _lifecycle = ModApi.Services.Lifecycle;
        _missions = ModApi.Services.Missions;
        _travel = ModApi.Services.Travel;

        // Subscribe first, then read the current state: a consumer loaded after readiness must not
        // miss the fact that a session already exists.
        _lifecycle.Changed += OnLifecycle;
        _missions.AvailabilityChanged += OnMissionAvailability;
        _missions.Transitioned += OnMissionTransition;
        _travel.AvailabilityChanged += OnTravelAvailability;
        _travel.Transitioned += OnTravel;

        Logger.LogInfo($"Session tracking: {_lifecycle.SessionTracking.Availability.Reason}; save outcomes: {_lifecycle.SaveOutcomes.Availability.Reason}");
        Logger.LogInfo($"Initial session: {_lifecycle.CurrentSession?.Id}, phase: {_lifecycle.CurrentSession?.Phase}");
        OnMissionAvailability(_missions.Availability);
        OnTravelAvailability(_travel.Availability);

        _hud = ModApi.Services.Hud.Register(Id, "panel", _ => { });
        RefreshPanel();
    }

    private void OnLifecycle(LifecycleEvent message)
    {
        _lifecycleEvents++;
        _lastLifecycle = $"{message.Kind} ({message.Session?.Phase})";
        Logger.LogInfo($"{message.Kind}: session={message.Session?.Id}, phase={message.Session?.Phase}, operation={message.OperationId}, destination={message.Destination}, detail={message.Detail}");
        // Observe only. Do not block, mutate an in-progress load/save, or retain vanilla references.
        RefreshPanel();
    }

    private void OnMissionTransition(MissionTransition transition)
    {
        // Ignore facts from a session that is no longer the live one, and from phases that are not
        // ready for observation. Stale facts are dropped rather than acted on.
        var current = _lifecycle?.CurrentSession;
        if (current?.Id != transition.Mission.SessionId ||
            current.Phase is not (SessionPhase.PlayerReady or SessionPhase.GameplayInitialized)) return;
        _missionTransitions++;
        _lastMission = transition.Kind.ToString();
        RefreshPanel();
    }

    private void OnTravel(TravelTransition transition)
    {
        if (_travel?.SessionId != transition.SessionId) return;
        _lastTravel = transition.Kind.ToString();
        if (transition.Kind == TravelTransitionKind.RouteCompleted) _routesCompleted++;
        RefreshPanel();
    }

    /// <summary>A terminal service fault cannot be repaired by loading a different save: stop observing.</summary>
    private void OnMissionAvailability(ServiceAvailability state)
    {
        if (state.Reason is ServiceUnavailableReason.ObserverFault or ServiceUnavailableReason.ApiStopped)
        { if (_missions != null) _missions.Transitioned -= OnMissionTransition; Logger.LogWarning("Mission observation stopped: " + state.Reason); }
        else if (!state.IsAvailable) Logger.LogInfo("Missions unavailable: " + state.Reason);
    }

    private void OnTravelAvailability(ServiceAvailability state)
    {
        if (state.Reason is ServiceUnavailableReason.ObserverFault or ServiceUnavailableReason.ApiStopped)
        { if (_travel != null) _travel.Transitioned -= OnTravel; Logger.LogWarning("Travel observation stopped: " + state.Reason); }
        else if (!state.IsAvailable) Logger.LogInfo("Travel unavailable: " + state.Reason);
    }

    private void RefreshPanel()
    {
        if (_hud == null) return;
        var session = _lifecycle?.CurrentSession;
        _hud.Update(null, new HudPanel("Observation",
            new[]
            {
                new HudRow("session", "Session: " + (session?.Phase.ToString() ?? "none"),
                    session?.Id.ToString() ?? "no current session",
                    "Read from ILifecycleService.CurrentSession; this mod never drives the session."),
                new HudRow("lifecycle", $"Lifecycle events: {_lifecycleEvents}", "last: " + _lastLifecycle,
                    "Every event is logged to BepInEx/LogOutput.log with its operation and detail."),
                new HudRow("missions", $"Mission transitions: {_missionTransitions}", "last: " + _lastMission,
                    "Transitions from a stale session or a not-ready phase are deliberately ignored."),
                new HudRow("travel", $"Routes completed: {_routesCompleted}", "last: " + _lastTravel,
                    "Only transitions matching the travel service's own session id are counted."),
            },
            closable: false));
    }

    private void OnDestroy()
    {
        if (_lifecycle != null) _lifecycle.Changed -= OnLifecycle;
        if (_missions != null) { _missions.AvailabilityChanged -= OnMissionAvailability; _missions.Transitioned -= OnMissionTransition; }
        if (_travel != null) { _travel.AvailabilityChanged -= OnTravelAvailability; _travel.Transitioned -= OnTravel; }
        var hud = _hud; _hud = null; hud?.Dispose();
        _lifecycle = null; _missions = null; _travel = null;
    }
}
