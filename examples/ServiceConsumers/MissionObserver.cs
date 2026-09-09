using System;

namespace VGModAPI.Examples;

/// <summary>Required-service domain logic. Callbacks observe facts; they do not mutate the game's active operation.</summary>
public sealed class MissionObserver : IDisposable
{
    private readonly IMissionService _missions;
    private readonly ILifecycleService _lifecycle;
    private readonly Action<MissionTransition> _observe;
    private readonly Action<ServiceAvailability> _report;
    private bool _disposed;

    public MissionObserver(IMissionService missions, ILifecycleService lifecycle,
        Action<MissionTransition> observe, Action<ServiceAvailability> report)
    {
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _observe = observe ?? throw new ArgumentNullException(nameof(observe));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        try
        {
            _missions.AvailabilityChanged += OnAvailability;
            _missions.Transitioned += OnTransition;
            OnAvailability(_missions.Availability);
        }
        catch { Dispose(); throw; }
    }

    private void OnAvailability(ServiceAvailability state)
    {
        if (_disposed) return;
        // A terminal service fault cannot be repaired by loading a different save.
        if (state.Reason is ServiceUnavailableReason.ObserverFault or ServiceUnavailableReason.ApiStopped) Dispose();
        _report(state);
    }

    private void OnTransition(MissionTransition transition)
    {
        if (_disposed || !_missions.Availability.IsAvailable) return;
        var current = _lifecycle.CurrentSession;
        if (current?.Id != transition.Mission.SessionId ||
            current.Phase is not (SessionPhase.PlayerReady or SessionPhase.GameplayInitialized)) return;
        try { _observe(transition); }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _missions.AvailabilityChanged -= OnAvailability;
        _missions.Transitioned -= OnTransition;
    }
}
