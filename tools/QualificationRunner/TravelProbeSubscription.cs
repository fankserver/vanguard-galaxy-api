using System;
using VGModAPI;

namespace VGModAPI.Qualification;

internal sealed class TravelProbeSubscription : IDisposable
{
    private readonly ITravelService _service;
    private Action<TravelTransition>? _handler;
    internal TravelProbeSubscription(ITravelService service, Action<TravelTransition> handler)
    { _service = service; _handler = handler; service.Transitioned += handler; }
    public void Dispose() { _service.Transitioned -= _handler; _handler = null; }
}

internal sealed class StationProbeSubscription : IDisposable
{
    private readonly IStationService _service;
    private Action<StationTransition>? _handler;
    internal StationProbeSubscription(IStationService service, Action<StationTransition> handler)
    { _service = service; _handler = handler; service.Transitioned += handler; }
    public void Dispose() { _service.Transitioned -= _handler; _handler = null; }
}
