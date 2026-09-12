using System;
using System.Runtime.CompilerServices;

namespace VGModAPI.Examples;

/// <summary>
/// Optional-dependency entry point with no API types in its members. The loader supplies no factory
/// when the API is absent/too old; the bridge is called only inside a catchable, non-inlined boundary.
/// </summary>
public sealed class OptionalTravelObserver : IDisposable
{
    private readonly IOptionalTravelListener? _listener;

    public OptionalTravelObserver(Func<object>? resolveTravel, Action<Guid> completed, Action<string> report)
    {
        if (completed == null) throw new ArgumentNullException(nameof(completed));
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (resolveTravel == null) return;
        try { _listener = OptionalTravelBridge.Attach(resolveTravel, completed, report); }
        catch (Exception error)
        {
            try { report("Optional travel observation unavailable: " + error.GetType().Name); }
            catch { /* A diagnostic failure must not turn an optional feature into a startup requirement. */ }
        }
    }

    public bool IsListening => _listener?.IsListening == true;
    public void Dispose() => _listener?.Dispose();
}

internal interface IOptionalTravelListener : IDisposable
{
    bool IsListening { get; }
}

internal static class OptionalTravelBridge
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static IOptionalTravelListener Attach(Func<object> resolve, Action<Guid> completed, Action<string> report)
        => new Listener((ITravelService)resolve(), completed, report);

    private sealed class Listener : IOptionalTravelListener
    {
        private readonly ITravelService _travel;
        private readonly Action<Guid> _completed;
        private readonly Action<string> _report;
        private bool _disposed;

        internal Listener(ITravelService travel, Action<Guid> completed, Action<string> report)
        {
            _travel = travel ?? throw new ArgumentNullException(nameof(travel));
            _completed = completed;
            _report = report;
            try
            {
                _travel.AvailabilityChanged += OnAvailability;
                _travel.Transitioned += OnTransition;
                OnAvailability(_travel.Availability);
            }
            catch { Dispose(); throw; }
        }

        public bool IsListening => !_disposed && _travel.Availability.IsAvailable;
        private void OnAvailability(ServiceAvailability state)
        {
            if (_disposed) return;
            if (state.Reason is ServiceUnavailableReason.ObserverFault or ServiceUnavailableReason.ApiStopped) Dispose();
            if (!state.IsAvailable) _report("Optional travel observation unavailable: " + state.Reason);
        }

        private void OnTransition(TravelTransition transition)
        {
            if (!IsListening || transition.Kind != TravelTransitionKind.RouteCompleted || _travel.SessionId != transition.SessionId) return;
            try { _completed(transition.SessionId); }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _travel.AvailabilityChanged -= OnAvailability;
            _travel.Transitioned -= OnTransition;
        }
    }
}
