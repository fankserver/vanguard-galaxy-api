using System;

namespace VGModAPI.Core;

internal abstract class ObservationServiceView : IServiceStatus
{
    protected readonly LifecycleHub Hub;
    private readonly IServiceStatus _status;
    protected ObservationServiceView(LifecycleHub hub, string feature)
    { Hub = hub; _status = hub.Services.Get(feature); }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    protected void RequireBoundSource(object? source)
    {
        if (source == null && Availability.IsAvailable)
            throw new ArgumentException("An available observation service requires its bound source.", nameof(source));
    }
    protected bool InSession(Guid sessionId)
    {
        var session = Hub.CurrentSession;
        return Availability.IsAvailable && session?.Id == sessionId &&
            session.Phase != SessionPhase.Invalidated && session.Phase != SessionPhase.Failed;
    }
}

internal sealed class TravelServiceView : ObservationServiceView, ITravelService, IDisposable
{
    private readonly ITravelEvents? _source;
    private readonly ServiceSubscriptions<TravelTransition> _events;
    internal TravelServiceView(LifecycleHub hub, ITravelEvents? source) : base(hub, "native-travel")
    {
        RequireBoundSource(source);
        _source = source;
        _events = new ServiceSubscriptions<TravelTransition>(hub, source == null ? null : source.Subscribe,
            fact => InSession(fact.SessionId), () => Availability.IsAvailable);
    }
    public Guid? SessionId
    {
        get
        {
            Hub.CheckThread();
            if (!Availability.IsAvailable) return null;
            var id = _source?.SessionId;
            return id.HasValue && InSession(id.Value) ? id : null;
        }
    }
    public TravelLocation? CurrentLocation => SessionId.HasValue ? _source?.CurrentLocation : null;
    public bool IsDispatchingCallbacks { get { Hub.CheckThread(); return Hub.IsDispatchingCallbacks || (Availability.IsAvailable && (_source?.IsDispatchingCallbacks ?? false)); } }
    public event Action<TravelTransition>? Transitioned { add => _events.Add(value); remove => _events.Remove(value); }
    public void Dispose() => _events.Dispose();
}

internal sealed class StationServiceView : ObservationServiceView, IStationService, IDisposable
{
    private readonly IStationEvents? _source;
    private readonly ServiceSubscriptions<StationTransition> _events;
    internal StationServiceView(LifecycleHub hub, IStationEvents? source) : base(hub, "native-travel")
    {
        RequireBoundSource(source);
        _source = source;
        _events = new ServiceSubscriptions<StationTransition>(hub, source == null ? null : source.Subscribe,
            fact => InSession(fact.SessionId), () => Availability.IsAvailable);
    }
    public Guid? SessionId
    {
        get
        {
            Hub.CheckThread();
            if (!Availability.IsAvailable) return null;
            var id = _source?.SessionId;
            return id.HasValue && InSession(id.Value) ? id : null;
        }
    }
    public bool IsDispatchingCallbacks { get { Hub.CheckThread(); return Hub.IsDispatchingCallbacks || (Availability.IsAvailable && (_source?.IsDispatchingCallbacks ?? false)); } }
    public event Action<StationTransition>? Transitioned { add => _events.Add(value); remove => _events.Remove(value); }
    public void Dispose() => _events.Dispose();
}
