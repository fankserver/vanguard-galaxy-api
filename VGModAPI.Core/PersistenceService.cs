using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class PersistenceService : ISaveDataService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PersistenceCoordinator? _coordinator;
    private readonly IServiceStatus _status;
    private readonly List<Registration> _registrations = new();
    private readonly Queue<(Registration Registration, SaveDataState State)> _notifications = new();
    private bool _disposed, _notifying, _cleaned;

    internal PersistenceService(LifecycleHub hub) : this(hub, null) { }

    internal PersistenceService(LifecycleHub hub, GenerationStore store, Func<string, string> canonical, Func<string, string> hashFile)
        : this(hub, new PersistenceCoordinator(hub, store, canonical, hashFile)) { }

    private PersistenceService(LifecycleHub hub, PersistenceCoordinator? coordinator)
    {
        _hub = hub;
        _coordinator = coordinator;
        _status = hub.Services.Get("save-data");
        if (coordinator != null) coordinator.StateChanged += PublishStates;
        _status.AvailabilityChanged += OnAvailabilityChanged;
        if (coordinator != null) hub.SetCapability("save-data", true, "Save storage initialized.");
        else
        {
            var diagnosis = _status.Availability;
            hub.SetCapability("save-data", false,
                diagnosis.IsAvailable ? "Save storage is unavailable." : diagnosis.Detail,
                diagnosis.IsAvailable ? ServiceUnavailableReason.BindingFailed : diagnosis.Reason);
        }
    }

    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }

    public SaveDataRegistrationResult Register(PersistenceProvider provider)
    {
        _hub.CheckThread();
        if (_disposed || _coordinator == null || !_status.Availability.IsAvailable)
            return new SaveDataRegistrationResult(SaveDataRegistrationStatus.Unavailable, detail: _status.Availability.Detail);
        if (provider == null) return new SaveDataRegistrationResult(SaveDataRegistrationStatus.InvalidProvider);
        OwnerSchemaCodec codec;
        try { codec = new OwnerSchemaCodec(provider.Owner, provider.SchemaVersion, provider.Validate, provider.Migrations); }
        catch (ArgumentException error)
        { return new SaveDataRegistrationResult(SaveDataRegistrationStatus.InvalidProvider, detail: error.GetType().Name); }
        var admission = _coordinator.Admission(codec.Owner);
        if (admission != SaveDataRegistrationStatus.Registered) return new SaveDataRegistrationResult(admission);
        return new SaveDataRegistrationResult(SaveDataRegistrationStatus.Registered, Register(provider, codec));
    }

    private Registration Register(PersistenceProvider provider, OwnerSchemaCodec codec)
    {
        _coordinator!.Register(codec, provider.Capture, payload => provider.Restore(_hub.CurrentSession!, payload));
        var registration = new Registration(this, provider.Owner);
        _registrations.Add(registration);
        return registration;
    }

    private void OnAvailabilityChanged(ServiceAvailability _) => PublishStates();

    private void PublishStates()
    {
        _hub.CheckThread();
        if (_cleaned) return;
        foreach (var registration in _registrations.ToArray())
        {
            var current = registration.State;
            var previous = registration.Published;
            if (current.Kind == previous.Kind && current.SessionId == previous.SessionId &&
                current.Reason == previous.Reason && current.Detail == previous.Detail) continue;
            registration.Published = current;
            _notifications.Enqueue((registration, current));
        }
        if (_notifying) return;
        _notifying = true;
        try
        {
            using var scope = _hub.EnterServiceDispatch();
            while (_notifications.Count != 0)
            {
                var next = _notifications.Dequeue();
                next.Registration.Notify(next.State);
            }
        }
        finally
        {
            _notifying = false;
            if (_disposed) FinishDispose();
        }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        _hub.SetCapability("save-data", false, "Save data service stopped.", ServiceUnavailableReason.ApiStopped);
        _coordinator?.Dispose();
        PublishStates();
        if (!_notifying) FinishDispose();
    }

    private void FinishDispose()
    {
        if (_cleaned) return;
        _cleaned = true;
        if (_coordinator != null) _coordinator.StateChanged -= PublishStates;
        _status.AvailabilityChanged -= OnAvailabilityChanged;
        foreach (var registration in _registrations) registration.CloseNotifications();
        _registrations.Clear(); _notifications.Clear();
    }

    private sealed class Registration : ISaveDataRegistration
    {
        private readonly PersistenceService _service;
        private readonly string _owner;
        private readonly ServiceNotifications<SaveDataState> _events;
        private bool _disposed;
        internal SaveDataState Published;
        internal Registration(PersistenceService service, string owner)
        {
            _service = service; _owner = owner;
            _events = new ServiceNotifications<SaveDataState>(service._hub.CheckThread, service._hub.ReportSubscriberFailure, service._hub.EnterServiceDispatch);
            Published = State;
        }
        public SaveDataState State
        {
            get
            {
                _service._hub.CheckThread();
                if (_disposed || _service._disposed || _service._hub.Services.IsStopping) return new SaveDataState(SaveDataStateKind.Disposed);
                var state = _service._coordinator!.State(_owner);
                var health = _service._status.Availability;
                return health.IsAvailable ? state : new SaveDataState(SaveDataStateKind.Blocked, state.SessionId,
                    SaveDataBlockReason.ServiceUnavailable, health.Detail);
            }
        }
        public bool CanRead => State.Kind == SaveDataStateKind.Ready;
        public bool CanMutate => CanRead && _service._coordinator!.MutationAllowed(_owner);
        public event Action<SaveDataState>? StateChanged
        {
            add
            {
                _service._hub.CheckThread();
                if (State.Kind == SaveDataStateKind.Disposed) throw new ObjectDisposedException(nameof(Registration));
                _events.Add(value);
            }
            remove => _events.Remove(value);
        }
        internal void Notify(SaveDataState state) => _events.Publish(state);
        internal void CloseNotifications() => _events.Dispose();
        public void Dispose()
        {
            _service._hub.CheckThread();
            if (_disposed) return;
            _disposed = true;
            _service._coordinator!.Unregister(_owner);
            _service._registrations.Remove(this);
            _events.Dispose();
        }
    }
}
