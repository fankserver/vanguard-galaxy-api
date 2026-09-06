using System;

namespace VGModAPI.Core;

internal sealed class PersistenceService : IPersistenceApi, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PersistenceCoordinator _coordinator;
    private bool _disposed;

    internal PersistenceService(LifecycleHub hub, GenerationStore store, Func<string, string> canonical, Func<string, string> hashFile)
    { _hub = hub; _coordinator = new PersistenceCoordinator(hub, store, canonical, hashFile); }

    public IPersistenceRegistration Register(PersistenceProvider provider)
    {
        _hub.CheckThread();
        if (_disposed) throw new ObjectDisposedException(nameof(PersistenceService));
        if (provider == null) throw new ArgumentNullException(nameof(provider));
        var codec = new OwnerSchemaCodec(provider.Owner, provider.SchemaVersion, provider.Validate, provider.Migrations);
        _coordinator.Register(codec, provider.Capture, payload => provider.Restore(_hub.CurrentSession!, payload));
        return new Registration(this, provider.Owner);
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _coordinator.Dispose(); _disposed = true;
    }

    private sealed class Registration : IPersistenceRegistration
    {
        private readonly PersistenceService _service;
        private readonly string _owner;
        private bool _disposed;
        internal Registration(PersistenceService service, string owner) { _service = service; _owner = owner; }
        public bool MutationAllowed
        { get { _service._hub.CheckThread(); return !_disposed && !_service._disposed && _service._coordinator.MutationAllowed(_owner); } }
        public string Status
        { get { _service._hub.CheckThread(); return _disposed || _service._disposed ? "inactive" : _service._coordinator.Status(_owner); } }
        public void Dispose()
        {
            _service._hub.CheckThread();
            if (_disposed) return;
            _service._coordinator.Unregister(_owner); _disposed = true;
        }
    }
}
