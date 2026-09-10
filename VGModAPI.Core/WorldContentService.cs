using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using VGModAPI.Core.Integration;

namespace VGModAPI.Core;

/// <summary>Internal engine surface retained for guard/reconciliation regressions; not part of the public modder API.</summary>
internal interface IWorldProviderEngine
{
    WorldSiteResult FindPersistentCombatSite(Guid expectedSessionId, WorldSiteReference reference);
    WorldSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y);
}

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
    private readonly AuthoredSiteRegistry? _siteDefinitions;
    private readonly AuthoredSiteCoordinator? _siteCoordinator;
    private event Action<Guid>? _sitesSettled;
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
    internal WorldContentService(LifecycleHub hub, WorldDefinitionRegistry definitions, WorldAuthoringGate authoring, Func<bool> canAuthor, Action? providerReleased = null, AmbientTrafficService? ambient = null, UnitProtectionService? protection = null, DroneBayService? droneBays = null, AuthoredSystemRegistry? authoredDefinitions = null, AuthoredSystemCoordinator? authoredCoordinator = null, AuthoredSiteRegistry? siteDefinitions = null, AuthoredSiteCoordinator? siteCoordinator = null)
    {
        _siteDefinitions = siteDefinitions;
        _siteCoordinator = siteCoordinator;
        siteCoordinator?.AttachSettled(session =>
        {
            var subscribers = _sitesSettled;
            if (subscribers == null) return;
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<Guid>)subscriber)(session); } catch { /* fail-open per subscriber */ } }
        });
        _hub = hub; _status = hub.Services.Get("world-authoring"); _definitions = definitions; _authoring = authoring; _canAuthor = canAuthor; _providerReleased = providerReleased;
        _ownsAmbient = ambient == null;
        _ambient = ambient ?? new AmbientTrafficService(hub);
        _ownsProtection = protection == null;
        _protection = protection ?? new UnitProtectionService(hub);
        _ownsDroneBays = droneBays == null;
        _droneBays = droneBays ?? new DroneBayService(hub);
        _authoredDefinitions = authoredDefinitions;
        // Deliver settled to every subscriber independently: one provider's faulty handler must not
        // starve sibling providers of their only aggregate reconstruction event for the session.
        if (authoredCoordinator != null) authoredCoordinator.AttachSettled(args =>
        {
            var subscribers = _authoredSettled;
            if (subscribers == null) return;
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try { ((Action<ReconstructionSettledEvent>)subscriber)(args); }
                catch { /* fail-open per subscriber; the coordinator reports its own faults */ }
            }
        });
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
        if (_siteCoordinator != null)
        {
            _siteCoordinator.BeginPass(session);
            try { _siteCoordinator.Reconcile(session); } catch { /* fail-open */ }
            finally { _siteCoordinator.EndPass(); }
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
        AuthoredSiteRegistry.Provider? sites = null;
        if (_siteDefinitions != null)
        {
            sites = _siteDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (sites == null) { provider.Dispose(); authored?.Dispose(); return null; }
        }
        return new Provider(this, provider, authored, sites);
    }
    private sealed class Provider : IWorldProvider, IWorldProviderEngine
    {
        private readonly Dictionary<(string LocalId, string OccurrenceKey), CombatSiteHandle> _sites = new();
        private readonly IDisposable _siteSubscription;
        private readonly WorldContentService _service;
        private readonly WorldDefinitionRegistry.Provider _provider;
        private readonly AuthoredSystemRegistry.Provider? _authored;
        private readonly AuthoredSiteRegistry.Provider? _authoredSites;
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredSiteHandle> _siteObjects = new();
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredSystemHandle> _objects = new();
        private readonly IDisposable _objectSubscription;
        private readonly Action<Guid> _refreshesEntry;
        private readonly Func<bool> _alive;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider, AuthoredSystemRegistry.Provider? authored, AuthoredSiteRegistry.Provider? authoredSites = null)
        {
            _service = service; _provider = provider; _authored = authored; _authoredSites = authoredSites;
            if (authoredSites != null) service._sitesSettled += ForwardSitesSettled;
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
            else
            {
                _objectSubscription = null!;
                _refreshesEntry = RefreshAuthoredObjects;
                service._authoredRefreshes.Add(_refreshesEntry);
            }
            _siteSubscription = service._hub.Subscribe("vgmodapi.combat-site-objects", e =>
            {
                if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                { _sites.Clear(); _siteObjects.Clear(); }
            });
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
            // Deliver to every consumer handler independently: one faulty handler must not starve the
            // rest of this provider's subscribers of the session's only aggregate reconstruction event.
            var subscribers = AuthoredSystemReconstructionSettled;
            if (subscribers == null) return;
            var settledEvent = new AuthoredSystemsSettledEvent(args.SessionId, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try { ((Action<AuthoredSystemsSettledEvent>)subscriber)(settledEvent); }
                catch { /* fail-open per subscriber */ }
            }
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
        /// <summary>Uniform occurrence-key contract: bounded, no control characters.</summary>
        internal static bool ValidOccurrenceKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key!.Length > 128) return false;
            foreach (char character in key) if (char.IsControl(character)) return false;
            return true;
        }

        /// <summary>Deterministic API-allocated native identity for an author-local occurrence key.</summary>
        internal static Guid SiteInstanceId(string providerId, string localId, string occurrenceKey)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(providerId + "\n" + localId + "\n" + occurrenceKey));
            var guid = new byte[16];
            Array.Copy(bytes, guid, 16);
            return new Guid(guid);
        }

        public ICombatSite? CreateCombatSite(string localId, string occurrenceKey, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            if (!_service._definitions.TryResolve(_provider, localId, out _)) return null;
            var instanceId = SiteInstanceId(ProviderId, localId, occurrenceKey);
            // Keyed reconciliation: an existing occurrence under this key is the occurrence; never a duplicate.
            var existing = FindPersistentCombatSite(session.Id, new WorldSiteReference(ProviderId, localId, instanceId));
            var result = existing.Succeeded ? existing : CreatePersistentCombatSite(session.Id, localId, instanceId, systemId, x, y);
            if (result.Status is not (WorldStatus.Succeeded or WorldStatus.Rejected)) return null;
            var handle = ObtainSite(localId, occurrenceKey, session.Id);
            handle.RecordAction(result.Status == WorldStatus.Succeeded
                ? new AuthoredActionResult(AuthoredActionStatus.Succeeded)
                : new AuthoredActionResult(AuthoredActionStatus.Rejected, "The native site could not be created."));
            handle.Refresh();
            return handle;
        }

        public ICombatSite? GetCombatSite(string localId, string occurrenceKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            var instanceId = SiteInstanceId(ProviderId, localId, occurrenceKey);
            if (!FindPersistentCombatSite(session.Id, new WorldSiteReference(ProviderId, localId, instanceId)).Succeeded) return null;
            var handle = ObtainSite(localId, occurrenceKey, session.Id);
            handle.Refresh();
            return handle;
        }

        private CombatSiteHandle ObtainSite(string localId, string occurrenceKey, Guid session)
        {
            var key = (localId, occurrenceKey);
            if (_sites.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new CombatSiteHandle(this, localId, occurrenceKey, session);
            _sites[key] = handle;
            return handle;
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

        public event Action<AuthoredSitesSettledEvent>? AuthoredSiteReconstructionSettled;
        private void ForwardSitesSettled(Guid session)
        {
            if (_authoredSites == null || _service._siteCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var reconstructed = new List<IAuthoredSite>();
            var failures = new List<AuthoredSiteFailure>();
            foreach (var row in _service._siteCoordinator.Occurrences(_authoredSites.Owner))
            {
                var handle = ObtainSiteHandle(row.LocalId, row.OccurrenceKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new AuthoredSiteFailure(handle,
                    state.Reason ?? _service._siteCoordinator.PendingReason(session)));
            }
            var subscribers = AuthoredSiteReconstructionSettled;
            if (subscribers == null) return;
            var settled = new AuthoredSitesSettledEvent(session, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<AuthoredSitesSettledEvent>)subscriber)(settled); } catch { /* fail-open per subscriber */ } }
        }

        public WorldStatus RegisterAuthoredSite(AuthoredSiteDefinition definition, AuthoredSiteDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (_authoredSites == null || _service._siteDefinitions == null || definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                var mapped = new AuthoredSiteDeclaration(definition);
                if (_service._siteDefinitions.TryResolve(_authoredSites, definition.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                var prior = previous == null ? null : new AuthoredSiteDeclaration(previous);
                return _service._siteDefinitions.Register(_authoredSites, mapped, prior) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }

        public IAuthoredSite? CreateAuthoredSite(string localId, string occurrenceKey, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return null;
            if (!_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            // The persistence envelope keys occurrences per (owner, local, key) across BOTH kinds; a
            // cross-kind collision must be refused here, not discovered at save time.
            if (_authored != null && _service._authoredCoordinator != null
                && _service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return null;
            var (status, _) = _service._siteCoordinator.Create(_authoredSites, session.Id, localId, occurrenceKey, systemId, x, y);
            if (status != WorldStatus.Succeeded && status != WorldStatus.Rejected) return null;
            if (_service._siteCoordinator.TryGetOccurrence(_authoredSites.Owner, localId, occurrenceKey) == null && status != WorldStatus.Rejected) return null;
            var handle = ObtainSiteHandle(localId, occurrenceKey, session.Id);
            handle.RecordAction(status == WorldStatus.Succeeded
                ? new AuthoredActionResult(AuthoredActionStatus.Succeeded)
                : new AuthoredActionResult(AuthoredActionStatus.Rejected, "The native site could not be created."));
            handle.Refresh();
            return handle;
        }

        public IAuthoredSite? GetAuthoredSite(string localId, string occurrenceKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            if (_service._siteCoordinator.TryGetOccurrence(_authoredSites.Owner, localId, occurrenceKey) == null) return null;
            var handle = ObtainSiteHandle(localId, occurrenceKey, session.Id);
            handle.Refresh();
            return handle;
        }

        public IReadOnlyList<IAuthoredSite> GetAuthoredSites(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return Array.Empty<IAuthoredSite>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IAuthoredSite>();
            var list = new List<IAuthoredSite>();
            foreach (var row in _service._siteCoordinator.Occurrences(_authoredSites.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainSiteHandle(row.LocalId, row.OccurrenceKey, session.Id));
            return list;
        }

        private AuthoredSiteHandle ObtainSiteHandle(string localId, string occurrenceKey, Guid session)
        {
            var key = (localId, occurrenceKey);
            if (_siteObjects.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new AuthoredSiteHandle(this, localId, occurrenceKey, session);
            _siteObjects[key] = handle;
            return handle;
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
            if (localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            // The persistence envelope keys occurrences per (owner, local, key) across BOTH kinds.
            if (_authoredSites != null && _service._siteCoordinator != null
                && _service._siteCoordinator.TryGetOccurrence(_authoredSites.Owner, localId, occurrenceKey) != null) return null;
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
            if (session == Guid.Empty || session != _service._hub.CurrentSession?.Id) return;
            foreach (var site in _sites.Values.ToArray()) if (site.Session == session) site.Refresh();
            foreach (var handle in _siteObjects.Values.ToArray()) if (handle.Session == session) handle.Refresh();
            if (_authored == null || _service._authoredCoordinator == null) return;
            foreach (var handle in _objects.Values.ToArray()) handle.Refresh();
        }
        public void Dispose()
        {
            _service._hub.CheckThread(); if (_disposed) return;
            if (_authored != null)
            {
                _service._authoredSettled -= ForwardSettled;
                _objectSubscription.Dispose();
            }
            if (_authoredSites != null) _service._sitesSettled -= ForwardSitesSettled;
            _service._authoredRefreshes.Remove(_refreshesEntry);
            _siteSubscription.Dispose();
            _sites.Clear();
            _siteObjects.Clear();
            _objects.Clear();
            _provider.Dispose(); _authored?.Dispose(); _authoredSites?.Dispose(); _disposed = true;
            _service._providerReleased?.Invoke();
        }

        /// <summary>The owned authored-site occurrence object; one instance per key per session.</summary>
        private sealed class AuthoredSiteHandle : IAuthoredSite
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _occurrenceKey;
            internal readonly Guid Session;
            private AuthoredSiteState _state = new(AuthoredSystemReconstructionStatus.Pending);
            private AuthoredActionResult _lastAction = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<IAuthoredSite>? _changed;
            internal AuthoredSiteHandle(Provider provider, string localId, string occurrenceKey, Guid session)
            { _provider = provider; _localId = localId; _occurrenceKey = occurrenceKey; Session = session; }
            public string OccurrenceKey => _occurrenceKey;
            public AuthoredSiteDefinition Definition
            {
                get
                {
                    if (_provider._service._siteDefinitions != null && _provider._authoredSites != null
                        && _provider._service._siteDefinitions.TryResolve(_provider._authoredSites, _localId, out var declaration) && declaration != null)
                        return declaration.ToDefinition();
                    // Honor the retained row's kind; only the declarative detail is unknown.
                    var row = _provider._service._siteCoordinator?.TryGetOccurrence(_provider._authoredSites?.Owner ?? "", _localId, _occurrenceKey);
                    return row?.Kind == AuthoredSiteKind.SalvageSite
                        ? AuthoredSiteDefinition.Salvage(_localId, row.Revision, "unknown", 1, "unknown", "unknown")
                        : AuthoredSiteDefinition.MiningField(_localId, row?.Revision ?? 1, "unknown", 1, 1);
                }
            }
            public AuthoredSiteState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? PoiId => State.PoiId;
            public AuthoredActionResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<IAuthoredSite>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(AuthoredActionResult result) => _lastAction = result;
            internal void Refresh()
            {
                // A replaced session freezes the last observed state; the handle never resolves against the replacement save.
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || _provider._authoredSites == null || _provider._service._siteCoordinator == null) return;
                var updated = _provider._service._siteCoordinator.ReconstructionState(_provider._authoredSites.Owner, _localId, _occurrenceKey);
                bool changed = _state.Status != updated.Status || _state.Reason != updated.Reason || _state.PoiId != updated.PoiId;
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
        }

        /// <summary>The owned combat-site occurrence object; one instance per key per session.</summary>
        private sealed class CombatSiteHandle : ICombatSite
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _occurrenceKey;
            internal readonly Guid Session;
            private CombatSiteState _state = new(AuthoredSystemReconstructionStatus.Pending);
            private AuthoredActionResult _lastAction = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<ICombatSite>? _changed;
            internal CombatSiteHandle(Provider provider, string localId, string occurrenceKey, Guid session)
            { _provider = provider; _localId = localId; _occurrenceKey = occurrenceKey; Session = session; }
            public string OccurrenceKey => _occurrenceKey;
            public WorldCombatSiteDefinition Definition
            {
                get
                {
                    if (_provider._service._definitions.TryResolve(_provider._provider, _localId, out var declaration) && declaration?.Definition is { } definition)
                        return new WorldCombatSiteDefinition(definition.LocalId, definition.Revision, definition.Name, definition.FactionId, definition.Level);
                    return new WorldCombatSiteDefinition(_localId, 1, "", "", 1);
                }
            }
            public CombatSiteState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? PoiId => State.PoiId;
            public AuthoredActionResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<ICombatSite>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(AuthoredActionResult result) => _lastAction = result;
            internal void Refresh()
            {
                // A replaced session freezes the last observed state; the handle never resolves against the replacement save.
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || !_provider._service._canAuthor()) return;
                var found = _provider.FindPersistentCombatSite(Session,
                    new WorldSiteReference(_provider.ProviderId, _localId, SiteInstanceId(_provider.ProviderId, _localId, _occurrenceKey)));
                var updated = found.Succeeded
                    ? new CombatSiteState(AuthoredSystemReconstructionStatus.Reconstructed, poiId: found.PoiId)
                    : new CombatSiteState(AuthoredSystemReconstructionStatus.Pending);
                bool changed = _state.Status != updated.Status || _state.PoiId != updated.PoiId;
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
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
