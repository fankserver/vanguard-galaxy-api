using System;
using VGModAPI;

namespace VGModAPI.Qualification;

internal sealed class BarProbeSubscription : IDisposable
{
    private readonly IBarService _service;
    private Action<BarRosterFinalized>? _handler;
    internal BarProbeSubscription(IBarService service, Action<BarRosterFinalized> handler)
    { _service = service; _handler = handler; service.RosterFinalized += handler; }
    public void Dispose() { _service.RosterFinalized -= _handler; _handler = null; }
}
