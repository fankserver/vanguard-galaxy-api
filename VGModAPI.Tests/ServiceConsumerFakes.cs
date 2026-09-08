using System;

namespace VGModAPI.Tests;

// Controlled collaborators for consumer examples, not implementations of the runtime notification engine.
internal class FakeServiceStatus : IServiceStatus
{
    public ServiceAvailability Availability { get; private set; } = ServiceAvailability.Available;
    public event Action<ServiceAvailability>? AvailabilityChanged;
    internal int AvailabilityListeners => AvailabilityChanged?.GetInvocationList().Length ?? 0;
    internal void SetAvailability(ServiceUnavailableReason reason)
    {
        Availability = new ServiceAvailability(reason);
        AvailabilityChanged?.Invoke(Availability);
    }
}

internal sealed class FakeLifecycleService : ILifecycleService
{
    public IServiceStatus SessionTracking { get; } = new FakeServiceStatus();
    public IServiceStatus SaveOutcomes { get; } = new FakeServiceStatus();
    public SessionSnapshot? CurrentSession { get; set; }
    public bool IsDispatchingCallbacks { get; set; }
    public event Action<LifecycleEvent>? Changed;
    internal void Emit(LifecycleEvent fact) => Changed?.Invoke(fact);
}

internal sealed class FakeMissionService : FakeServiceStatus, IMissionService
{
    public IServiceStatus IdentityContinuity { get; } = new FakeServiceStatus();
    public event Action<MissionTransition>? Transitioned;
    internal int TransitionListeners => Transitioned?.GetInvocationList().Length ?? 0;
    internal void Emit(MissionTransition fact) => Transitioned?.Invoke(fact);
    public bool TryGetNative(MissionSnapshot snapshot, out object? native) { native = null; return false; }
}

internal sealed class FakeTravelService : FakeServiceStatus, ITravelService
{
    public Guid? SessionId { get; set; }
    public TravelLocation? CurrentLocation { get; set; }
    public bool IsDispatchingCallbacks { get; set; }
    public event Action<TravelTransition>? Transitioned;
    internal int TransitionListeners => Transitioned?.GetInvocationList().Length ?? 0;
    internal void Emit(TravelTransition fact) => Transitioned?.Invoke(fact);
}

internal sealed class FakeSaveDataService : FakeServiceStatus, ISaveDataService
{
    internal FakeSaveRegistration Registration { get; } = new();
    internal PersistenceProvider? Provider { get; private set; }
    internal int RegisterCalls { get; private set; }
    internal SaveDataRegistrationStatus Result { get; set; } = SaveDataRegistrationStatus.Registered;
    public SaveDataRegistrationResult Register(PersistenceProvider provider)
    {
        ++RegisterCalls;
        Provider = provider;
        return new SaveDataRegistrationResult(Result, Result == SaveDataRegistrationStatus.Registered ? Registration : null);
    }
}

internal sealed class FakeSaveRegistration : ISaveDataRegistration
{
    private bool _read, _mutate, _dispatching;
    internal bool Disposed { get; private set; }
    internal bool SaveInFlight { get; set; }
    internal int DisposeCalls { get; private set; }
    internal int Listeners => StateChanged?.GetInvocationList().Length ?? 0;
    public SaveDataState State { get; private set; } = new(SaveDataStateKind.Inactive);
    public bool CanRead => !Disposed && _read;
    public bool CanMutate => !Disposed && _mutate && !_dispatching && !SaveInFlight;
    public event Action<SaveDataState>? StateChanged;

    internal void SetState(SaveDataState state, bool read = false, bool mutate = false)
    {
        State = state; _read = read; _mutate = mutate;
        _dispatching = true;
        try { StateChanged?.Invoke(state); }
        finally { _dispatching = false; }
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        ++DisposeCalls;
        State = new SaveDataState(SaveDataStateKind.Disposed);
        StateChanged = null;
    }
}
