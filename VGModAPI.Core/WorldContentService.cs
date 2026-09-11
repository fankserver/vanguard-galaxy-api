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
    private readonly AuthoredWormholePairRegistry? _wormholeDefinitions;
    private readonly AuthoredWormholePairCoordinator? _wormholeCoordinator;
    private event Action<Guid>? _wormholesSettled;
    private readonly AuthoredSiteRegistry? _siteDefinitions;
    private readonly AuthoredSiteCoordinator? _siteCoordinator;
    private event Action<Guid>? _sitesSettled;
    private readonly Dictionary<(string Owner, string Local, string Key), CombatSiteKeyRow> _combatKeys = new();
    private Guid _combatKeySession;
    private bool _combatSettledOnce;
    private event Action<Guid>? _combatSettled;
    internal void RestoreCombatKeys(Guid session, CombatSiteKeyRow[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || _hub.CurrentSession?.Id != session) throw new System.IO.InvalidDataException("Stale combat-key restore.");
        _combatKeys.Clear(); _combatKeySession = session; _combatSettledOnce = false;
        if (rows != null) foreach (var row in rows) _combatKeys[(row.Owner, row.LocalId, row.OccurrenceKey)] = row;
    }
    internal CombatSiteKeyRow[] CaptureCombatKeys()
    {
        _hub.CheckThread();
        return _combatKeySession == _hub.CurrentSession?.Id ? _combatKeys.Values.ToArray() : Array.Empty<CombatSiteKeyRow>();
    }
    private void EnsureCombatKeySession(Guid session)
    {
        if (_combatKeySession == session) return;
        _combatKeys.Clear(); _combatKeySession = session; _combatSettledOnce = false;
    }
    internal bool CombatKeyClaimed(string owner, string localId, string occurrenceKey)
    { _hub.CheckThread(); return _combatKeySession == _hub.CurrentSession?.Id && _combatKeys.ContainsKey((owner, localId, occurrenceKey)); }

    private readonly AuthoredShipRegistry? _shipDefinitions;
    private readonly AuthoredShipCoordinator? _shipCoordinator;
    private readonly VGModAPI.Core.Integration.IEncounterNative? _encounters;
    private event Action<Guid>? _shipsSettled;
    private readonly bool _ownsAmbient, _ownsProtection, _ownsDroneBays;
    private readonly List<Action<Guid>> _authoredRefreshes = new();
    private readonly List<Action<string, (string Owner, string LocalId, string OccurrenceKey)[]>> _dissolveNotifiers = new();
    private event Action<ReconstructionSettledEvent>? _authoredSettled;
    public IAmbientTrafficService AmbientTraffic { get { _hub.CheckThread(); return _ambient; } }
    public IUnitProtectionService UnitProtection { get { _hub.CheckThread(); return _protection; } }
    public IDroneBayService DroneBays { get { _hub.CheckThread(); return _droneBays; } }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    private bool _disposed;
    internal WorldContentService(LifecycleHub hub, WorldDefinitionRegistry definitions, WorldAuthoringGate authoring, Func<bool> canAuthor, Action? providerReleased = null, AmbientTrafficService? ambient = null, UnitProtectionService? protection = null, DroneBayService? droneBays = null, AuthoredSystemRegistry? authoredDefinitions = null, AuthoredSystemCoordinator? authoredCoordinator = null, AuthoredSiteRegistry? siteDefinitions = null, AuthoredSiteCoordinator? siteCoordinator = null, AuthoredShipRegistry? shipDefinitions = null, AuthoredShipCoordinator? shipCoordinator = null, VGModAPI.Core.Integration.IEncounterNative? encounters = null, AuthoredWormholePairRegistry? wormholeDefinitions = null, AuthoredWormholePairCoordinator? wormholeCoordinator = null)
    {
        _wormholeDefinitions = wormholeDefinitions;
        _wormholeCoordinator = wormholeCoordinator;
        wormholeCoordinator?.AttachSettled(session =>
        {
            var subscribers = _wormholesSettled; if (subscribers == null) return;
            foreach (var subscriber in subscribers.GetInvocationList()) try { ((Action<Guid>)subscriber)(session); } catch { }
        });
        _siteDefinitions = siteDefinitions;
        _siteCoordinator = siteCoordinator;
        _shipDefinitions = shipDefinitions;
        _shipCoordinator = shipCoordinator;
        _encounters = encounters;
        shipCoordinator?.AttachSettled(session =>
        {
            var subscribers = _shipsSettled;
            if (subscribers == null) return;
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<Guid>)subscriber)(session); } catch { /* fail-open per subscriber */ } }
        });
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
        if (_wormholeCoordinator != null)
        {
            try { _wormholeCoordinator.Reconcile(session); } catch { /* fail-open */ }
        }
        if (_siteCoordinator != null)
        {
            _siteCoordinator.BeginPass(session);
            try { _siteCoordinator.Reconcile(session); } catch { /* fail-open */ }
            finally { _siteCoordinator.EndPass(); }
        }
        if (_shipCoordinator != null)
        {
            try { _shipCoordinator.Reconcile(session); } catch { /* fail-open */ }
        }
        if (!_combatSettledOnce && _combatKeySession == session && _hub.CurrentSession?.Phase == SessionPhase.GameplayInitialized)
        {
            _combatSettledOnce = true;
            var subscribers = _combatSettled;
            if (subscribers != null)
                foreach (var subscriber in subscribers.GetInvocationList())
                { try { ((Action<Guid>)subscriber)(session); } catch { /* fail-open per subscriber */ } }
        }
        foreach (var refresh in _authoredRefreshes.ToArray())
        {
            try { refresh(session); } catch { /* one provider's fault must not block the others */ }
        }
    }
    /// <summary>
    /// After a successful native pocket dissolution: drop the retained authored-site rows inside the
    /// removed system (their native POIs were removed with it) and let every provider terminally mark
    /// and release its owned occurrence objects for that pocket.
    /// </summary>
    private void PocketDissolved(string systemId)
    {
        var droppedSites = _siteCoordinator?.DropOccurrencesInSystem(systemId) ?? Array.Empty<(string, string, string)>();
        foreach (var notify in _dissolveNotifiers.ToArray())
        {
            try { notify(systemId, droppedSites); } catch { /* one provider's fault must not block the others */ }
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
        AuthoredWormholePairRegistry.Provider? wormholes = null;
        if (_wormholeDefinitions != null)
        {
            wormholes = _wormholeDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (wormholes == null) { provider.Dispose(); authored?.Dispose(); return null; }
        }
        AuthoredSiteRegistry.Provider? sites = null;
        if (_siteDefinitions != null)
        {
            sites = _siteDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (sites == null) { provider.Dispose(); authored?.Dispose(); wormholes?.Dispose(); return null; }
        }
        AuthoredShipRegistry.Provider? ships = null;
        if (_shipDefinitions != null)
        {
            ships = _shipDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (ships == null) { provider.Dispose(); authored?.Dispose(); wormholes?.Dispose(); sites?.Dispose(); return null; }
        }
        return new Provider(this, provider, authored, sites, ships, wormholes);
    }
    private sealed class Provider : IWorldProvider, IWorldProviderEngine
    {
        private readonly Dictionary<(string LocalId, string OccurrenceKey), CombatSiteHandle> _sites = new();
        private readonly IDisposable _siteSubscription;
        private readonly WorldContentService _service;
        private readonly WorldDefinitionRegistry.Provider _provider;
        private readonly AuthoredSystemRegistry.Provider? _authored;
        private readonly AuthoredSiteRegistry.Provider? _authoredSites;
        private readonly AuthoredWormholePairRegistry.Provider? _wormholes;
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredWormholePairHandle> _wormholeObjects = new();
        private readonly AuthoredShipRegistry.Provider? _authoredShips;
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredShipHandle> _shipObjects = new();
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredSiteHandle> _siteObjects = new();
        private readonly Dictionary<(string LocalId, string OccurrenceKey), AuthoredSystemHandle> _objects = new();
        private readonly IDisposable _objectSubscription;
        private readonly Action<Guid> _refreshesEntry;
        private readonly Action<string, (string Owner, string LocalId, string OccurrenceKey)[]> _dissolveEntry;
        private readonly Func<bool> _alive;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider, AuthoredSystemRegistry.Provider? authored, AuthoredSiteRegistry.Provider? authoredSites = null, AuthoredShipRegistry.Provider? authoredShips = null, AuthoredWormholePairRegistry.Provider? wormholes = null)
        {
            _service = service; _provider = provider; _authored = authored; _authoredSites = authoredSites; _authoredShips = authoredShips; _wormholes = wormholes;
            if (wormholes != null) service._wormholesSettled += ForwardWormholesSettled;
            if (authoredSites != null) service._sitesSettled += ForwardSitesSettled;
            if (authoredShips != null) service._shipsSettled += ForwardShipsSettled;
            service._combatSettled += ForwardCombatSettled;
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
            _dissolveEntry = OnPocketDissolved;
            service._dissolveNotifiers.Add(_dissolveEntry);
            _siteSubscription = service._hub.Subscribe("vgmodapi.combat-site-objects", e =>
            {
                if (e.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
                { _sites.Clear(); _siteObjects.Clear(); _shipObjects.Clear(); _wormholeObjects.Clear(); }
            });
        }
        private void ResetObjects()
        {
            _objects.Clear();
            _service._hub.CheckThread();
        }
        /// <summary>Terminally marks and releases this provider's site objects dropped with a dissolved pocket.</summary>
        private void OnPocketDissolved(string systemId, (string Owner, string LocalId, string OccurrenceKey)[] droppedSites)
        {
            if (_disposed || _authoredSites == null) return;
            foreach (var dropped in droppedSites)
            {
                if (dropped.Owner != _authoredSites.Owner) continue;
                if (_siteObjects.TryGetValue((dropped.LocalId, dropped.OccurrenceKey), out var handle))
                {
                    _siteObjects.Remove((dropped.LocalId, dropped.OccurrenceKey));
                    handle.MarkDissolved();
                }
            }
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

        public event Action<CombatSitesSettledEvent>? CombatSiteReconstructionSettled;
        private void ForwardCombatSettled(Guid session)
        {
            if (_disposed || _service._hub.CurrentSession?.Id != session) return;
            var reconstructed = new List<ICombatSite>();
            var failures = new List<CombatSiteFailure>();
            foreach (var row in _service._combatKeys.Values.ToArray())
            {
                if (row.Owner != ProviderId) continue;
                var handle = ObtainSite(row.LocalId, row.OccurrenceKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new CombatSiteFailure(handle, state.Reason ?? AuthoredSystemFailureReason.NativeMissing));
            }
            var subscribers = CombatSiteReconstructionSettled;
            if (subscribers == null) return;
            var settled = new CombatSitesSettledEvent(session, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<CombatSitesSettledEvent>)subscriber)(settled); } catch { /* fail-open per subscriber */ } }
        }

        public IReadOnlyList<ICombatSite> GetCombatSites(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || localId == null) return Array.Empty<ICombatSite>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || _service._combatKeySession != session.Id) return Array.Empty<ICombatSite>();
            var list = new List<ICombatSite>();
            foreach (var row in _service._combatKeys.Values.ToArray())
                if (row.Owner == ProviderId && string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                {
                    var handle = ObtainSite(row.LocalId, row.OccurrenceKey, session.Id);
                    handle.Refresh();
                    list.Add(handle);
                }
            return list;
        }

        public ICombatSite? CreateCombatSite(string localId, string occurrenceKey, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            if (OtherKindOwnsKey(exceptShips: false, localId, occurrenceKey)) return null;
            if (!_service._definitions.TryResolve(_provider, localId, out _)) return null;
            var instanceId = SiteInstanceId(ProviderId, localId, occurrenceKey);
            // Keyed reconciliation: an existing occurrence under this key is the occurrence; never a duplicate.
            var existing = FindPersistentCombatSite(session.Id, new WorldSiteReference(ProviderId, localId, instanceId));
            var result = existing.Succeeded ? existing : CreatePersistentCombatSite(session.Id, localId, instanceId, systemId, x, y);
            if (result.Status is not (WorldStatus.Succeeded or WorldStatus.Rejected)) return null;
            if (result.Status == WorldStatus.Succeeded)
            {
                _service.EnsureCombatKeySession(session.Id);
                _service._combatKeys[(ProviderId, localId, occurrenceKey)] = new CombatSiteKeyRow(ProviderId, localId, occurrenceKey, instanceId);
            }
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
            // The persistence envelope keys occurrences per (owner, local, key) across ALL kinds; a
            // cross-kind collision must be refused here, not discovered at save time.
            if (_authored != null && _service._authoredCoordinator != null
                && _service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsOccurrence(_authoredShips.Owner, localId, occurrenceKey)) return null;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, occurrenceKey)) return null;
            if (CombatKeyOwnsKey(localId, occurrenceKey)) return null;
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

        public event Action<AuthoredShipsSettledEvent>? AuthoredShipReconstructionSettled;
        private void ForwardShipsSettled(Guid session)
        {
            if (_authoredShips == null || _service._shipCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var reconstructed = new List<IAuthoredShip>();
            var failures = new List<AuthoredShipFailure>();
            foreach (var row in _service._shipCoordinator.Occurrences(_authoredShips.Owner))
            {
                var handle = ObtainShipHandle(row.LocalId, row.OccurrenceKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new AuthoredShipFailure(handle, state.Reason ?? _service._shipCoordinator.PendingReason(session)));
            }
            var subscribers = AuthoredShipReconstructionSettled;
            if (subscribers == null) return;
            var settled = new AuthoredShipsSettledEvent(session, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<AuthoredShipsSettledEvent>)subscriber)(settled); } catch { /* fail-open per subscriber */ } }
        }

        public WorldStatus RegisterAuthoredShip(AuthoredShipDefinition definition, AuthoredShipDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (_authoredShips == null || _service._shipDefinitions == null || definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                var mapped = new AuthoredShipDeclaration(definition);
                if (_service._shipDefinitions.TryResolve(_authoredShips, definition.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                var prior = previous == null ? null : new AuthoredShipDeclaration(previous);
                return _service._shipDefinitions.Register(_authoredShips, mapped, prior) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }

        public IAuthoredShip? CreateAuthoredShip(string localId, string occurrenceKey, string stationPoiId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return null;
            if (!_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            // The persistence envelope keys occurrences per (owner, local, key) across ALL kinds.
            if (OtherKindOwnsKey(exceptShips: true, localId, occurrenceKey) || CombatKeyOwnsKey(localId, occurrenceKey)) return null;
            var (status, _) = _service._shipCoordinator.Create(_authoredShips, session.Id, localId, occurrenceKey, stationPoiId);
            if (status != WorldStatus.Succeeded && status != WorldStatus.Rejected) return null;
            var handle = ObtainShipHandle(localId, occurrenceKey, session.Id);
            handle.RecordAction(status == WorldStatus.Succeeded
                ? new AuthoredActionResult(AuthoredActionStatus.Succeeded)
                : new AuthoredActionResult(AuthoredActionStatus.Rejected, "The moored ship could not be created."));
            handle.Refresh();
            return handle;
        }

        public IAuthoredShip? GetAuthoredShip(string localId, string occurrenceKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidOccurrenceKey(occurrenceKey)) return null;
            if (_service._shipCoordinator.TryGetOccurrence(_authoredShips.Owner, localId, occurrenceKey) == null) return null;
            var handle = ObtainShipHandle(localId, occurrenceKey, session.Id);
            handle.Refresh();
            return handle;
        }

        private bool OtherKindOwnsKey(bool exceptShips, string localId, string occurrenceKey)
        {
            if (_authored != null && _service._authoredCoordinator != null
                && _service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return true;
            if (_authoredSites != null && _service._siteCoordinator != null
                && _service._siteCoordinator.ContainsOccurrence(_authoredSites.Owner, localId, occurrenceKey)) return true;
            if (!exceptShips && _authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsOccurrence(_authoredShips.Owner, localId, occurrenceKey)) return true;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, occurrenceKey)) return true;
            return false;
        }
        private bool CombatKeyOwnsKey(string localId, string occurrenceKey)
        {
            return _service.CombatKeyClaimed(ProviderId, localId, occurrenceKey);
        }

        public IReadOnlyList<IAuthoredShip> GetAuthoredShips(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return Array.Empty<IAuthoredShip>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IAuthoredShip>();
            var list = new List<IAuthoredShip>();
            foreach (var row in _service._shipCoordinator.Occurrences(_authoredShips.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainShipHandle(row.LocalId, row.OccurrenceKey, session.Id));
            return list;
        }

        private AuthoredShipHandle ObtainShipHandle(string localId, string occurrenceKey, Guid session)
        {
            var key = (localId, occurrenceKey);
            if (_shipObjects.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new AuthoredShipHandle(this, localId, occurrenceKey, session);
            _shipObjects[key] = handle;
            return handle;
        }

        /// <summary>The owned moored-ship occurrence object; one instance per key per session.</summary>
        private sealed class AuthoredShipHandle : IAuthoredShip
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _occurrenceKey;
            internal readonly Guid Session;
            private AuthoredShipState _state = new(AuthoredSystemReconstructionStatus.Pending);
            private AuthoredActionResult _lastAction = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<IAuthoredShip>? _changed;
            internal AuthoredShipHandle(Provider provider, string localId, string occurrenceKey, Guid session)
            { _provider = provider; _localId = localId; _occurrenceKey = occurrenceKey; Session = session; }
            public string OccurrenceKey => _occurrenceKey;
            public AuthoredShipDefinition Definition
            {
                get
                {
                    if (_provider._service._shipDefinitions != null && _provider._authoredShips != null
                        && _provider._service._shipDefinitions.TryResolve(_provider._authoredShips, _localId, out var declaration) && declaration != null)
                        return declaration.ToDefinition();
                    var row = _provider._service._shipCoordinator?.TryGetOccurrence(_provider._authoredShips?.Owner ?? "", _localId, _occurrenceKey);
                    return new AuthoredShipDefinition(_localId, row?.Revision ?? 1, "unknown", "unknown", "unknown", 0, 0);
                }
            }
            public AuthoredShipState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? UnitId => State.UnitId;
            public AuthoredActionResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<IAuthoredShip>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(AuthoredActionResult result) => _lastAction = result;
            internal void Refresh()
            {
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || _provider._authoredShips == null || _provider._service._shipCoordinator == null) return;
                var updated = _provider._service._shipCoordinator.ReconstructionState(_provider._authoredShips.Owner, _localId, _occurrenceKey);
                bool changed = _state.Status != updated.Status || _state.Reason != updated.Reason || _state.UnitId != updated.UnitId;
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
        }

        public EncounterSpawnResult SpawnEncounter(string poiId, EncounterComposition composition)
        {
            _service._hub.CheckThread();
            if (composition == null) throw new ArgumentNullException(nameof(composition));
            if (_disposed || _service._disposed || _service._encounters == null)
                return new EncounterSpawnResult(AuthoredActionStatus.Unavailable, detail: "Encounter integration is unavailable.");
            if (!_service._canAuthor()) return new EncounterSpawnResult(AuthoredActionStatus.Unavailable, detail: "World authoring is unavailable.");
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new EncounterSpawnResult(AuthoredActionStatus.NotReady, detail: "The world is not in a safely actionable state yet.");
            if (string.IsNullOrWhiteSpace(poiId) || poiId.Length > 4096)
                return new EncounterSpawnResult(AuthoredActionStatus.Rejected, detail: "A POI identity is required.");
            var outcome = _service._encounters.Spawn(session.Id, poiId, composition);
            if (outcome == null) return new EncounterSpawnResult(AuthoredActionStatus.Unavailable, detail: "The encounter could not be scheduled.");
            var (scheduled, detail) = outcome.Value;
            int requested = 0;
            foreach (var wave in composition.Waves) requested += wave.Count;
            return scheduled == requested
                ? new EncounterSpawnResult(AuthoredActionStatus.Succeeded, scheduled)
                : new EncounterSpawnResult(AuthoredActionStatus.Rejected, scheduled,
                    detail.Length > 0 ? detail : "The native trigger scheduled a different unit count than authored.");
        }

        public event Action<AuthoredWormholePairsSettledEvent>? AuthoredWormholePairReconstructionSettled;
        private void ForwardWormholesSettled(Guid session)
        {
            if (_wormholes == null || _service._wormholeCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var good = new List<IAuthoredWormholePair>(); var failed = new List<IAuthoredWormholePair>();
            foreach (var row in _service._wormholeCoordinator.Occurrences(_wormholes.Owner))
            {
                var handle = ObtainWormhole(row.LocalId, row.OccurrenceKey, session); handle.Refresh();
                if (handle.State.Reconstructed) good.Add(handle); else failed.Add(handle);
            }
            var subscribers = AuthoredWormholePairReconstructionSettled; if (subscribers == null) return;
            var args = new AuthoredWormholePairsSettledEvent(session, good, failed);
            foreach (var subscriber in subscribers.GetInvocationList()) try { ((Action<AuthoredWormholePairsSettledEvent>)subscriber)(args); } catch { }
        }
        public WorldStatus RegisterAuthoredWormholePair(AuthoredWormholePairDefinition definition, AuthoredWormholePairDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldStatus.NotReady;
            if (_wormholes == null || definition == null) return WorldStatus.InvalidDefinition;
            try
            {
                if (_service._wormholeDefinitions!.TryResolve(_wormholes, definition.LocalId, out _)) return WorldStatus.DuplicateDefinition;
                return _wormholes.Register(definition, previous) ? WorldStatus.Succeeded : WorldStatus.Rejected;
            }
            catch (ArgumentException) { return WorldStatus.InvalidDefinition; }
        }
        public IAuthoredWormholePair? CreateAuthoredWormholePair(string localId, string occurrenceKey, string firstSystemId, string secondSystemId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _wormholes == null || _service._wormholeCoordinator == null || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks || !ValidOccurrenceKey(occurrenceKey)) return null;
            if (_authored != null && _service._authoredCoordinator != null && _service._authoredCoordinator.ContainsOccurrence(_authored.Owner, localId, occurrenceKey)) return null;
            if (_authoredSites != null && _service._siteCoordinator != null && _service._siteCoordinator.ContainsOccurrence(_authoredSites.Owner, localId, occurrenceKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null && _service._shipCoordinator.ContainsOccurrence(_authoredShips.Owner, localId, occurrenceKey)) return null;
            if (CombatKeyOwnsKey(localId, occurrenceKey)) return null;
            var result = _service._wormholeCoordinator.Create(_wormholes, session.Id, localId, occurrenceKey, firstSystemId, secondSystemId);
            return result.Row == null ? null : ObtainWormhole(localId, occurrenceKey, session.Id);
        }
        public IAuthoredWormholePair? GetAuthoredWormholePair(string localId, string occurrenceKey)
        {
            _service._hub.CheckThread(); if (_wormholes == null || _service._wormholeCoordinator?.TryGet(_wormholes.Owner, localId, occurrenceKey) == null) return null;
            var session = _service._hub.CurrentSession; return session == null ? null : ObtainWormhole(localId, occurrenceKey, session.Id);
        }
        public IReadOnlyList<IAuthoredWormholePair> GetAuthoredWormholePairs(string localId)
        {
            _service._hub.CheckThread(); if (_wormholes == null || _service._wormholeCoordinator == null || _service._hub.CurrentSession is not { } session) return Array.Empty<IAuthoredWormholePair>();
            return _service._wormholeCoordinator.Occurrences(_wormholes.Owner).Where(r => r.LocalId == localId).Select(r => (IAuthoredWormholePair)ObtainWormhole(r.LocalId, r.OccurrenceKey, session.Id)).ToArray();
        }
        private AuthoredWormholePairHandle ObtainWormhole(string localId, string occurrenceKey, Guid session)
        {
            var key = (localId, occurrenceKey); if (_wormholeObjects.TryGetValue(key, out var found)) return found;
            var handle = new AuthoredWormholePairHandle(this, localId, occurrenceKey, session); _wormholeObjects.Add(key, handle); handle.Refresh(); return handle;
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
            // The persistence envelope keys occurrences per (owner, local, key) across ALL kinds.
            if (_authoredSites != null && _service._siteCoordinator != null
                && _service._siteCoordinator.ContainsOccurrence(_authoredSites.Owner, localId, occurrenceKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsOccurrence(_authoredShips.Owner, localId, occurrenceKey)) return null;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, occurrenceKey)) return null;
            if (CombatKeyOwnsKey(localId, occurrenceKey)) return null;
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
            var handle = new AuthoredSystemHandle(_service, _authored!, _service._authoredCoordinator!, _alive, localId, occurrenceKey, session,
                () => _objects.Remove(key));
            _objects[key] = handle;
            handle.Refresh();   // seed the cached state without firing Changed
            return handle;
        }
        private void RefreshAuthoredObjects(Guid session)
        {
            if (session == Guid.Empty || session != _service._hub.CurrentSession?.Id) return;
            foreach (var site in _sites.Values.ToArray()) if (site.Session == session) site.Refresh();
            foreach (var handle in _siteObjects.Values.ToArray()) if (handle.Session == session) handle.Refresh();
            foreach (var handle in _shipObjects.Values.ToArray()) if (handle.Session == session) handle.Refresh();
            foreach (var handle in _wormholeObjects.Values.ToArray()) if (handle.Session == session) handle.Refresh();
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
            if (_authoredShips != null) _service._shipsSettled -= ForwardShipsSettled;
            if (_wormholes != null) _service._wormholesSettled -= ForwardWormholesSettled;
            _service._combatSettled -= ForwardCombatSettled;
            _service._authoredRefreshes.Remove(_refreshesEntry);
            _service._dissolveNotifiers.Remove(_dissolveEntry);
            _siteSubscription.Dispose();
            _sites.Clear();
            _siteObjects.Clear();
            _shipObjects.Clear();
            _wormholeObjects.Clear();
            _objects.Clear();
            _provider.Dispose(); _authored?.Dispose(); _authoredSites?.Dispose(); _authoredShips?.Dispose(); _wormholes?.Dispose(); _disposed = true;
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
            /// <summary>The pocket containing this site dissolved; the object is terminal for its session.</summary>
            internal void MarkDissolved()
            {
                if (_dissolved) return;
                _dissolved = true;
                bool changed = _state.Status != AuthoredSystemReconstructionStatus.Dissolved;
                _state = new AuthoredSiteState(AuthoredSystemReconstructionStatus.Dissolved);
                if (changed) _changed?.Invoke(this);
            }
            private bool _dissolved;
            internal void Refresh()
            {
                if (_dissolved) return;
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

        private sealed class AuthoredWormholePairHandle : IAuthoredWormholePair
        {
            private readonly Provider _provider; private readonly string _localId, _key; internal Guid Session { get; }
            private AuthoredWormholePairState _state = new(AuthoredSystemReconstructionStatus.Pending);
            private AuthoredActionResult _last = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<IAuthoredWormholePair>? _changed;
            internal AuthoredWormholePairHandle(Provider provider, string localId, string key, Guid session)
            { _provider = provider; _localId = localId; _key = key; Session = session; }
            public string OccurrenceKey => _key;
            public AuthoredWormholePairDefinition Definition
            {
                get
                {
                    if (_provider._wormholes != null && _provider._service._wormholeDefinitions != null &&
                        _provider._service._wormholeDefinitions.TryResolve(_provider._wormholes, _localId, out var d) && d != null)
                        return new(d.LocalId, d.Revision, d.Name);
                    return new(_localId, 1, "unknown");
                }
            }
            public AuthoredWormholePairState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? FirstWormholePoiId => State.FirstWormholePoiId;
            public string? SecondWormholePoiId => State.SecondWormholePoiId;
            public AuthoredActionResult LastAction => _last;
            public event Action<IAuthoredWormholePair>? Changed { add => _changed += value; remove => _changed -= value; }
            public AuthoredActionResult SetOpen(bool open)
            {
                _provider._service._hub.CheckThread();
                if (_provider._disposed || _provider._service._disposed || _provider._wormholes == null || _provider._service._wormholeCoordinator == null)
                    return _last = new(AuthoredActionStatus.Unavailable);
                if (_provider._service._hub.CurrentSession?.Id != Session) return _last = new(AuthoredActionStatus.GameEnded);
                if (_provider._service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _provider._service._hub.IsDispatchingCallbacks)
                    return _last = new(AuthoredActionStatus.NotReady);
                var status = _provider._service._wormholeCoordinator.SetOpen(_provider._wormholes, Session, _localId, _key, open);
                return _last = new(status == WorldStatus.Succeeded ? AuthoredActionStatus.Succeeded : status == WorldStatus.Unavailable ? AuthoredActionStatus.Unavailable : AuthoredActionStatus.Rejected);
            }
            internal void Refresh()
            {
                if (_provider._service._hub.CurrentSession?.Id != Session || _provider._wormholes == null || _provider._service._wormholeCoordinator == null) return;
                var updated = _provider._service._wormholeCoordinator.State(_provider._wormholes, _localId, _key);
                bool changed = updated.Status != _state.Status || updated.Reason != _state.Reason || updated.FirstWormholePoiId != _state.FirstWormholePoiId || updated.SecondWormholePoiId != _state.SecondWormholePoiId;
                _state = updated; if (changed) _changed?.Invoke(this);
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
            private bool _dissolved;
            private readonly Action _evict;
            private AuthoredActionResult _lastAction = new(AuthoredActionStatus.NotReady, "No action has been taken yet on this occurrence.");
            private event Action<IAuthoredSystem>? _changed;

            internal AuthoredSystemHandle(WorldContentService service, AuthoredSystemRegistry.Provider authored,
                AuthoredSystemCoordinator coordinator, Func<bool> alive, string localId, string occurrenceKey, Guid session, Action evict)
            { _service = service; _authored = authored; _coordinator = coordinator; _alive = alive;
                _localId = localId; _occurrenceKey = occurrenceKey; _session = session; _evict = evict; }

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
                    if (_dissolved) return new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Dissolved);
                    if (!_seeded && _service._hub.CurrentSession?.Id == _session && _session != Guid.Empty)
                    { try { _state = _coordinator.ReconstructionState(_authored, Reference); _seeded = true; } catch { } }
                    return _state ?? new AuthoredSystemReconstructionState(AuthoredSystemReconstructionStatus.Pending);
                }
            }
            public string? SystemId => _dissolved ? null : _state?.SystemId;
            public string? EntranceGatePoiId => _dissolved ? null : _state?.EntranceGatePoiId;
            public string? PocketGatePoiId => _dissolved ? null : _state?.PocketGatePoiId;
            public AuthoredActionResult LastAction => _lastAction;
            public event Action<IAuthoredSystem>? Changed { add => _changed += value; remove => _changed -= value; }

            private AuthoredSystemReference Reference => new(_authored.Owner, _localId, _occurrenceKey);
            private bool IsCurrentSession() => _session != Guid.Empty && _service._hub.CurrentSession?.Id == _session;

            /// <summary>Uniform per-action gating shared by every occurrence action; null means actionable.</summary>
            private AuthoredActionResult? GateAction()
            {
                AuthoredActionResult Fail(AuthoredActionStatus status, string detail) => _lastAction = new AuthoredActionResult(status, detail);
                if (_dissolved) return Fail(AuthoredActionStatus.Rejected, "The occurrence was dissolved; create the key again for a fresh pocket.");
                if (!_alive()) return Fail(AuthoredActionStatus.Unavailable, "The provider lease is no longer active.");
                if (!IsCurrentSession()) return Fail(AuthoredActionStatus.GameEnded, "The owning session ended or was replaced; re-obtain the occurrence for the live game.");
                if (!_service._canAuthor()) return Fail(AuthoredActionStatus.Unavailable, "World authoring is unavailable.");
                var session = _service._hub.CurrentSession;
                if (session == null || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                    return Fail(AuthoredActionStatus.NotReady, "The world is not in a safely actionable state yet.");
                if (_service._authoredCoordinator == null) return Fail(AuthoredActionStatus.Unavailable, "Authored systems are unavailable.");
                return null;
            }

            public AuthoredActionResult SetEntranceOpen(bool open)
            {
                _service._hub.CheckThread();
                if (GateAction() is { } refused) return refused;
                var status = _service._authoredCoordinator!.SetOpen(_authored, _session, Reference, open);
                return _lastAction = new AuthoredActionResult(ToActionStatus(status), "");
            }

            public AuthoredActionResult Dissolve()
            {
                _service._hub.CheckThread();
                if (GateAction() is { } refused) return refused;
                // Never orphan combat-site records: their removal is not supported, so their presence refuses dissolution.
                var row = _service._authoredCoordinator!.TryGetOccurrence(_authored.Owner, _localId, _occurrenceKey);
                if (row != null && _service._authoring != null)
                {
                    var contains = _service._authoring.AnyInSystem(row.SystemId);
                    if (contains == null)
                        return _lastAction = new AuthoredActionResult(AuthoredActionStatus.NotReady,
                            "Combat-site save data is not ready to prove the pocket is empty of combat sites.");
                    if (contains == true)
                        return _lastAction = new AuthoredActionResult(AuthoredActionStatus.Rejected,
                            "The pocket still contains combat sites; they cannot be removed with it.");
                }
                var (status, detail, systemId) = _service._authoredCoordinator.Dissolve(_authored, _session, Reference);
                if (status != WorldStatus.Succeeded) return _lastAction = new AuthoredActionResult(ToActionStatus(status), detail);
                _dissolved = true;
                _evict();
                if (systemId != null) _service.PocketDissolved(systemId);
                return _lastAction = new AuthoredActionResult(AuthoredActionStatus.Succeeded);
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
                if (_dissolved) return;
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
