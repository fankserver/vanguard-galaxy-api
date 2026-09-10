using System;
using System.Collections.Generic;
using System.Linq;
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
    private readonly List<Action<Guid>> _authoredRefreshes = new();
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

    /// <summary>
    /// Periodic reconciled-invariant maintenance: converges authored gate state via the coordinator, then
    /// refreshes every provider's owned occurrence objects so their Changed events fire on their own
    /// transitions (e.g. Pending → Reconstructed once native construction surfaces the pocket).
    /// </summary>
    internal void MaintainAuthoredSystems(Guid session)
    {
        _hub.CheckThread();
        if (_authoredCoordinator != null)
        {
            try { _authoredCoordinator.Reconcile(session); } catch { /* fail-open; gate convergence is best-effort */ }
        }
        foreach (var refresh in _authoredRefreshes.ToArray())
        {
            try { refresh(session); } catch { /* one provider's fault must not block the others */ }
        }
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
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredSystemHandle> _objects = new();
        private readonly IDisposable _objectSubscription;
        private readonly Action<Guid> _refreshesEntry;
        private readonly Func<bool> _alive;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider, AuthoredSystemRegistry.Provider? authored)
        {
            _service = service; _provider = provider; _authored = authored;
            _alive = () => !_disposed && !_service._disposed;
            if (authored != null)
            {
                service._authoredSettled += ForwardSettled;
                _objectSubscription = service._hub.Subscribe("vgmodapi.authored-objects", e =>
                {
                    if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == service._hub.CurrentSession?.Id) ResetObjects();
                    else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == service._hub.CurrentSession?.Id) ResetObjects();
                });
                _refreshesEntry = RefreshAuthoredObjects;
                service._authoredRefreshes.Add(_refreshesEntry);
            }
            else { _objectSubscription = null!; _refreshesEntry = _ => { }; }
        }
        private void ResetObjects()
        {
            _objects.Clear();
            _service._hub.CheckThread();
        }
        public event Action<AuthoredSystemsSettledEvent>? AuthoredSystemReconstructionSettled;
        private void ForwardSettled(ReconstructionSettledEvent args)
        {
            if (_authored == null || _service._authoredCoordinator == null) return;
            if (_service._hub.CurrentSession?.Id != args.SessionId) return;
            // Failures come from the coordinator's own settle classification (it maps Pending → NativeMissing
            // or PersistenceUnavailable, which the raw object State does not carry). Inflate the owned objects.
            var failures = new List<AuthoredSystemFailure>();
            var failedKeys = new System.Collections.Generic.HashSet<(string Local, string Key)>();
            foreach (var failure in args.Failures)
            {
                var reference = failure.Reference;
                if (reference == null || reference.ProviderId != _authored.Owner) continue;
                var handle = ObtainHandle(reference.LocalId, reference.OccurrenceKey, args.SessionId);
                handle.Refresh();
                failures.Add(new AuthoredSystemFailure(handle, failure.Reason));
                failedKeys.Add((reference.LocalId, reference.OccurrenceKey));
            }
            var reconstructed = new List<IAuthoredSystem>();
            foreach (var row in _service._authoredCoordinator.Occurrences(_authored.Owner))
            {
                if (failedKeys.Contains((row.LocalId, row.OccurrenceKey))) continue;
                var handle = ObtainHandle(row.LocalId, row.OccurrenceKey, args.SessionId);
                handle.Refresh();   // Changed fires for each transitioned occurrence
                if (handle.State.Reconstructed) reconstructed.Add(handle);
            }
            AuthoredSystemReconstructionSettled?.Invoke(new AuthoredSystemsSettledEvent(args.SessionId, reconstructed, failures));
        }
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
        public IAuthoredSystem? CreateAuthoredSystem(string localId, string occurrenceKey, string anchorSystemId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return null;
            if (!_service._canAuthor() || _disposed || _service._disposed) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null) return null;
            var result = _service._authoredCoordinator.Create(_authored, session.Id, localId, occurrenceKey, anchorSystemId);
            if (result.Status != WorldStatus.Succeeded && result.Status != WorldStatus.Rejected) return null;
            if (!_service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return null;
            return ObtainHandle(localId, occurrenceKey, session.Id);
        }
        public IReadOnlyList<IAuthoredSystem> GetAuthoredSystems(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return Array.Empty<IAuthoredSystem>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IAuthoredSystem>();
            var list = new List<IAuthoredSystem>();
            foreach (var row in _service._authoredCoordinator.Occurrences(_authored.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainHandle(row.LocalId, row.OccurrenceKey, session.Id));
            return list;
        }
        public IAuthoredSystem? GetAuthoredSystem(string localId, string occurrenceKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return null;
            if (!_service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return null;
            return ObtainHandle(localId, occurrenceKey, session.Id);
        }
        private AuthoredSystemHandle ObtainHandle(string localId, string occurrenceKey, Guid session)
        {
            var key = (localId, occurrenceKey);
            if (_objects.TryGetValue(key, out var existing)) return existing;
            var handle = new AuthoredSystemHandle(_service, _authored!, _service._authoredCoordinator!, _alive, localId, occurrenceKey, session);
            _objects[key] = handle;
            handle.Refresh();   // seed the cached state without firing Changed
            return handle;
        }
        private void RefreshAuthoredObjects(Guid session)
        {
            if (session == Guid.Empty || session != _service._hub.CurrentSession?.Id || _authored == null || _service._authoredCoordinator == null) return;
            foreach (var handle in _objects.Values.ToArray()) handle.Refresh();
        }
        public void Dispose()
        {
            _service._hub.CheckThread(); if (_disposed) return;
            if (_authored != null)
            {
                _service._authoredSettled -= ForwardSettled;
                _service._authoredRefreshes.Remove(_refreshesEntry);
                _objectSubscription.Dispose();
            }
            _objects.Clear();
            _provider.Dispose(); _authored?.Dispose(); _disposed = true;
            _service._providerReleased?.Invoke();
        }

        /// <summary>The owned occurrence object exposed to consumers; one instance per key per session.</summary>
        private sealed class AuthoredSystemHandle : IAuthoredSystem
        {
            private readonly WorldContentService _service;
            private readonly AuthoredSystemRegistry.Provider _authored;
            private readonly AuthoredSystemCoordinator _coordinator;
            private readonly Func<bool> _alive;
            private readonly string _localId;
            private readonly string _occurrenceKey;
            private readonly Guid _session;
            private AuthoredSystemReconstructionState _state = null!;
            private bool _seeded;
            private AuthoredActionResult _lastAction = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<IAuthoredSystem>? _changed;

            internal AuthoredSystemHandle(WorldContentService service, AuthoredSystemRegistry.Provider authored,
                AuthoredSystemCoordinator coordinator, Func<bool> alive, string localId, string occurrenceKey, Guid session)
            { _service = service; _authored = authored; _coordinator = coordinator; _alive = alive;
                _localId = localId; _occurrenceKey = occurrenceKey; _session = session; }

            public string OccurrenceKey => _occurrenceKey;
            public AuthoredSystemDefinition Definition
            {
                get
                {
                    if (_service._authoredDefinitions != null && _service._authoredDefinitions.TryResolve(_authored, _localId, out var declaration) && declaration != null)
                        return new AuthoredSystemDefinition(declaration.LocalId, declaration.Revision, declaration.Name);
                    var revision = _coordinator.TryGetOccurrence(_authored.Owner, _localId, _occurrenceKey)?.Revision ?? 1;
                    return new AuthoredSystemDefinition(_localId, revision, "");
                }
            }
            public AuthoredSystemReconstructionState State
            {
                get
                {
                    if (!_seeded && _service._hub.CurrentSession?.Id == _session && _session != Guid.Empty)
                    { try { _state = _coordinator.ReconstructionState(_authored, Reference); _seeded = true; } catch { } }
                    return _state ?? new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
                }
            }
            public string? SystemId => _state?.SystemId;
            public string? EntranceGatePoiId => _state?.EntranceGatePoiId;
            public string? PocketGatePoiId => _state?.PocketGatePoiId;
            public AuthoredActionResult LastAction => _lastAction;
            public event Action<IAuthoredSystem>? Changed { add => _changed += value; remove => _changed -= value; }

            private AuthoredSystemReference Reference => new(_authored.Owner, _localId, _occurrenceKey);
            private bool IsCurrentSession() => _session != Guid.Empty && _service._hub.CurrentSession?.Id == _session;

            public AuthoredActionResult SetEntranceOpen(bool open)
            {
                _service._hub.CheckThread();
                AuthoredActionResult Fail(AuthoredActionStatus status, string detail) => _lastAction = new AuthoredActionResult(status, detail);
                if (!_alive()) return Fail(AuthoredActionStatus.Unavailable, "The provider lease is no longer active.");
                if (!IsCurrentSession()) return Fail(AuthoredActionStatus.GameEnded, "The owning session ended or was replaced; re-obtain the occurrence for the live game.");
                if (!_service._canAuthor()) return Fail(AuthoredActionStatus.Unavailable, "World authoring is unavailable.");
                var session = _service._hub.CurrentSession;
                if (session == null || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                    return Fail(AuthoredActionStatus.NotReady, "The world is not in a safely actionable state yet.");
                if (_service._authoredCoordinator == null) return Fail(AuthoredActionStatus.Unavailable, "Authored systems are unavailable.");
                var status = _service._authoredCoordinator.SetOpen(_authored, _session, Reference, open);
                return Fail(ToActionStatus(status), "");
            }
            private static AuthoredActionStatus ToActionStatus(WorldStatus status) => status switch
            {
                WorldStatus.Succeeded => AuthoredActionStatus.Succeeded,
                WorldStatus.NotReady => AuthoredActionStatus.NotReady,
                WorldStatus.Rejected or WorldStatus.NotRegistered or WorldStatus.InvalidDefinition or WorldStatus.DuplicateDefinition => AuthoredActionStatus.Rejected,
                _ => AuthoredActionStatus.Unavailable
            };

            internal void Refresh()
            {
                if (_service._hub.CurrentSession?.Id != _session || _session == Guid.Empty) return;
                AuthoredSystemReconstructionState updated;
                try { updated = _coordinator.ReconstructionState(_authored, Reference); }
                catch { return; }
                if (!_seeded) { _state = updated; _seeded = true; return; }
                bool changed = StateChanged(_state, updated);
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
            private static bool StateChanged(AuthoredSystemReconstructionState a, AuthoredSystemReconstructionState b)
                => a.Status != b.Status || a.Reason != b.Reason || a.SystemId != b.SystemId
                    || a.EntranceGatePoiId != b.EntranceGatePoiId || a.PocketGatePoiId != b.PocketGatePoiId;
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
