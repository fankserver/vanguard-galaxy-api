using System;
using VGModAPI;

namespace VGModAPI.Qualification;

internal sealed class CraftingJobProbeSubscription : IDisposable
{
    private readonly ICraftingJobService _service;
    private Action<CraftingJobEvent>? _handler;
    internal CraftingJobProbeSubscription(ICraftingJobService service, Action<CraftingJobEvent> handler)
    { _service = service; _handler = handler; service.Changed += handler; }
    public void Dispose() { _service.Changed -= _handler; _handler = null; }
}
