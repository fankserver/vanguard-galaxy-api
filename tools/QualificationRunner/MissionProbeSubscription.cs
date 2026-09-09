using System;
using VGModAPI;

namespace VGModAPI.Qualification;

internal sealed class MissionProbeSubscription : IDisposable
{
    private readonly IMissionService _service;
    private Action<MissionTransition>? _handler;
    internal MissionProbeSubscription(IMissionService service, Action<MissionTransition> handler)
    {
        _service = service;
        _handler = handler;
        service.Transitioned += handler;
    }
    public void Dispose()
    {
        _service.Transitioned -= _handler;
        _handler = null;
    }
}
