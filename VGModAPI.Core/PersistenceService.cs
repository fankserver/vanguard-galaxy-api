using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class PersistenceService : IPersistenceApi, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PersistenceCoordinator _coordinator;
    private readonly IServiceStatus _status;
    private readonly List<Registration> _registrations = new();
    private readonly Queue<(Registration Registration, SaveDataState State)> _notifications = new();
    private bool _disposed, _notifying, _cleaned;

    internal PersistenceService(LifecycleHub hub, GenerationStore store, Func<string, string> canonical, Func<string, string> hashFile)
    {
        _hub = hub; _coordinator = new PersistenceCoordinator(hub, store, canonical, hashFile);
        _status = hub.Services.Get("save-data");
        _coordinator.StateChanged += PublishStates;
        _status.AvailabilityChanged += OnAvailabilityChanged;
    }

    public IPersistenceRegistration Register(PersistenceProvider provider)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(PersistenceService));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        var codec = new OwnerSchemaCodec(provider.Owner, provider.SchemaVersion, provider.Validate, provider.Migrations);
        return Register(provider, codec);
    }

    internal SaveDataRegistrationResult RegisterData(PersistenceProvider provider)
    {
        _hub.CheckThread();
        if (_disposed || !_status.Availability.IsAvailable)
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
        _coordinator.Register(codec, provider.Capture, payload => provider.Restore(_hub.CurrentSession!, payload));
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
        _hub.SetCapability("save-data", false, "Persistence service stopped.", ServiceUnavailableReason.ObserverFault);
        _coordinator.Dispose();
        PublishStates();
        if (!_notifying) FinishDispose();
    }

    private void FinishDispose()
    {
        if (_cleaned) return;
        _cleaned = true;
        _coordinator.StateChanged -= PublishStates;
        _status.AvailabilityChanged -= OnAvailabilityChanged;
        foreach (var registration in _registrations) registration.CloseNotifications();
        _registrations.Clear(); _notifications.Clear();
    }

    private sealed class Registration : IPersistenceRegistration, IPersistenceReadiness, ISaveDataRegistration
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
        public bool MutationAllowed
        { get { _service._hub.CheckThread(); return !_disposed && !_service._disposed && _service._coordinator.MutationAllowed(_owner); } }
        public bool StateReady
        { get { _service._hub.CheckThread(); return !_disposed && !_service._disposed && _service._coordinator.StateReady(_owner); } }
        public string Status
        { get { _service._hub.CheckThread(); return _disposed || _service._disposed ? "inactive" : _service._coordinator.Status(_owner); } }
        public SaveDataState State
        {
            get
            {
                _service._hub.CheckThread();
                if (_disposed || _service._disposed || _service._hub.Services.IsStopping) return new SaveDataState(SaveDataStateKind.Disposed);
                var state = _service._coordinator.State(_owner);
                var health = _service._status.Availability;
                return health.IsAvailable ? state : new SaveDataState(SaveDataStateKind.Blocked, state.SessionId,
                    SaveDataBlockReason.ServiceUnavailable, health.Detail);
            }
        }
        public bool CanRead => State.Kind == SaveDataStateKind.Ready;
        public bool CanMutate => CanRead && MutationAllowed;
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
            _service._coordinator.Unregister(_owner);
            _service._registrations.Remove(this);
            _events.Dispose();
        }
    }
}

internal sealed class SaveDataServiceView : ObservationServiceView, ISaveDataService
{
    private readonly PersistenceService? _source;
    internal SaveDataServiceView(LifecycleHub hub, PersistenceService? source) : base(hub, "save-data")
    { RequireBoundSource(source); _source = source; }
    public SaveDataRegistrationResult Register(PersistenceProvider provider)
    {
        Hub.CheckThread();
        return _source?.RegisterData(provider) ?? new SaveDataRegistrationResult(SaveDataRegistrationStatus.Unavailable, detail: Availability.Detail);
    }
}
