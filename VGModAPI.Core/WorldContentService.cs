using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using VGModAPI.Core.Integration;

namespace VGModAPI.Core;

/// <summary>Authenticated Unity-free declaration/creation facade; operations enforce live binding, session and save-data readiness.</summary>
internal sealed class WorldContentService : IWorldService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly WorldDefinitionRegistry _definitions;
    private readonly WorldAuthoringGate _authoring;
    private readonly Func<bool> _canAuthor;
    private readonly Action? _providerReleased;
    private readonly IServiceStatus _status;
    private readonly AmbientTrafficService _ambient;
    private readonly UnitProtectionService _protection;
    private readonly DroneBayService _droneBays;
    private readonly AuthoredSystemRegistry? _authoredDefinitions;
    private readonly AuthoredSystemCoordinator? _authoredCoordinator;
    private readonly bool _ownsAmbient, _ownsProtection, _ownsDroneBays;
    private event Action<ReconstructionSettledEvent>? _authoredSettled;
    public IAmbientTrafficService AmbientTraffic { get { _hub.CheckThread(); return _ambient; } }
    public IUnitProtectionService UnitProtection { get { _hub.CheckThread(); return _protection; } }
    public IDroneBayService DroneBays { get { _hub.CheckThread(); return _droneBays; } }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    private bool _disposed;
    internal WorldContentService(LifecycleHub hub, WorldDefinitionRegistry definitions, WorldAuthoringGate authoring, Func<bool> canAuthor, Action? providerReleased = null, AmbientTrafficService? ambient = null, UnitProtectionService? protection = null, DroneBayService? droneBays = null, AuthoredSystemRegistry? authoredDefinitions = null, AuthoredSystemCoordinator? authoredCoordinator = null)
    {
        _hub = hub; _status = hub.Services.Get("world-authoring"); _definitions = definitions; _authoring = authoring; _canAuthor = canAuthor; _providerReleased = providerReleased;
        _ownsAmbient = ambient == null;
        _ambient = ambient ?? new AmbientTrafficService(hub);
        _ownsProtection = protection == null;
        _protection = protection ?? new UnitProtectionService(hub);
        _ownsDroneBays = droneBays == null;
        _droneBays = droneBays ?? new DroneBayService(hub);
        _authoredDefinitions = authoredDefinitions;
        if (authoredCoordinator != null) authoredCoordinator.AttachSettled(args => _authoredSettled?.Invoke(args));
        _authoredCoordinator = authoredCoordinator;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IWorldProvider? AcquireProvider(object pluginInstance)
    {
        _hub.CheckThread(); if (_disposed || _hub.CurrentSession != null) return null;
        var provider = _definitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
        if (provider == null || _disposed) return null;
        AuthoredSystemRegistry.Provider? authored = null;
        if (_authoredDefinitions != null)
        {
            authored = _authoredDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (authored == null) { provider.Dispose(); return null; }
        }
        return new Provider(this, provider, authored);
    }
    private sealed class Provider : IWorldProvider
    {
        private readonly WorldContentService _service;
        private readonly WorldDefinitionRegistry.Provider _provider;
        private readonly AuthoredSystemRegistry.Provider? _authored;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider, AuthoredSystemRegistry.Provider? authored)
        { _service = service; _provider = provider; _authored = authored; if (authored != null) service._authoredSettled += ForwardSettled; }
        public event Action<ReconstructionSettledEvent>? AuthoredSystemReconstructionSettled;
        private void ForwardSettled(ReconstructionSettledEvent args) => AuthoredSystemReconstructionSettled?.Invoke(args);
        public string ProviderId => _provider.Owner;
        public WorldStatus Register(WorldCombatSiteDefinition definition, WorldCombatSiteDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                var native = new WorldCombatDefinition(definition.LocalId, definition.Revision, definition.Name, definition.FactionId, definition.Level);
                if (_service._definitions.TryResolve(_provider, native.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                var prior = previous == null ? null : new WorldCombatDefinition(previous.LocalId, previous.Revision, previous.Name, previous.FactionId, previous.Level);
                return _provider.Register(native, prior) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }
        public WorldSiteResult FindPersistentCombatSite(Guid expectedSessionId, WorldSiteReference reference)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new WorldSiteResult(WorldStatus.UnknownProvider);
            if (reference == null || reference.ProviderId != ProviderId) return new WorldSiteResult(WorldStatus.NotRegistered);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new WorldSiteResult(WorldStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId) return new WorldSiteResult(WorldStatus.NotReady);
            try
            {
                var record = _service._authoring.TryFind(_provider, expectedSessionId, reference.LocalId, reference.InstanceId,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed);
                return record == null ? new WorldSiteResult(WorldStatus.NotRegistered) :
                    new WorldSiteResult(WorldStatus.Succeeded, new WorldSiteReference(record.Identity.Owner, record.Identity.LocalId, record.Identity.InstanceId), record.Identity.NativeId);
            }
            catch (ArgumentException) { return new WorldSiteResult(WorldStatus.InvalidDefinition); }
        }
        public WorldSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new WorldSiteResult(WorldStatus.UnknownProvider);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new WorldSiteResult(WorldStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId ||
                _service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new WorldSiteResult(WorldStatus.NotReady);
            if (localId == null || !_service._definitions.TryResolve(_provider, localId, out _)) return new WorldSiteResult(WorldStatus.NotRegistered);
            try
            {
                var identity = new WorldObjectIdentity(new ContentDeclaration(ProviderId, localId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent), instanceId);
                var success = new WorldSiteResult(WorldStatus.Succeeded, new WorldSiteReference(ProviderId, localId, instanceId), identity.NativeId);
                return _service._authoring.TryCreate(_provider, expectedSessionId, localId, instanceId, systemId, x, y,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed) == null
                    ? new WorldSiteResult(WorldStatus.Rejected) : success;
            }
            catch (ArgumentException) { return new WorldSiteResult(WorldStatus.InvalidDefinition); }
        }

        public WorldStatus RegisterAuthoredSystem(AuthoredSystemDefinition definition, AuthoredSystemDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (_authored == null || definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                if (_service._authoredDefinitions!.TryResolve(_authored, definition.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                return _authored.Register(definition, previous) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }
        public AuthoredSystemResult CreateAuthoredSystem(Guid expectedSessionId, string localId, string occurrenceKey, string anchorSystemId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new AuthoredSystemResult(WorldStatus.UnknownProvider);
            if (_authored == null || _service._authoredCoordinator == null) return new AuthoredSystemResult(WorldStatus.NotReady);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new AuthoredSystemResult(WorldStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId ||
                _service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new AuthoredSystemResult(WorldStatus.NotReady);
            if (localId == null) return new AuthoredSystemResult(WorldStatus.NotRegistered);
            return _service._authoredCoordinator.Create(_authored, expectedSessionId, localId, occurrenceKey, anchorSystemId);
        }
        public WorldStatus SetAuthoredSystemEntranceOpen(Guid expectedSessionId, AuthoredSystemReference reference, bool open)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_authored == null || _service._authoredCoordinator == null) return WorldStatus.NotReady;
            if (!_service._canAuthor() || _disposed || _service._disposed) return WorldStatus.Unavailable;
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId) return WorldStatus.NotReady;
            if (reference == null) return WorldStatus.NotRegistered;
            return _service._authoredCoordinator.SetOpen(_authored, expectedSessionId, reference, open);
        }
        public AuthoredSystemReconstructionState GetAuthoredSystemReconstructionState(AuthoredSystemReference reference)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
            if (_authored == null || _service._authoredCoordinator == null) return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
            return _service._authoredCoordinator.ReconstructionState(_authored, reference);
        }
        public void Dispose()
        {
            _service._hub.CheckThread(); if (_disposed) return;
            if (_authored != null) _service._authoredSettled -= ForwardSettled;
            _provider.Dispose(); _authored?.Dispose(); _disposed = true;
            _service._providerReleased?.Invoke();
        }
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        if (_ownsAmbient) _ambient.Dispose();
        if (_ownsProtection) _protection.Dispose();
        if (_ownsDroneBays) _droneBays.Dispose();
        var health = _status.Availability;
        _hub.SetCapability("world-authoring", false, health.IsAvailable ? "World service stopped." : health.Detail,
            health.IsAvailable ? ServiceUnavailableReason.ApiStopped : health.Reason);
    }
}
