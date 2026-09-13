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
    CombatSiteResult FindPersistentCombatSite(Guid expectedSessionId, CombatSiteReference reference);
    CombatSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y);
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
    private readonly PocketSystemRegistry? _authoredDefinitions;
    private readonly PocketSystemCoordinator? _authoredCoordinator;
    private readonly WormholePairRegistry? _wormholeDefinitions;
    private readonly WormholePairCoordinator? _wormholeCoordinator;
    private event Action<Guid>? _wormholesSettled;
    private readonly ResourceSiteRegistry? _siteDefinitions;
    private readonly ResourceSiteCoordinator? _siteCoordinator;
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
        if (rows != null) foreach (var row in rows) _combatKeys[(row.Owner, row.LocalId, row.PoiKey)] = row;
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
    internal bool CombatKeyClaimed(string owner, string localId, string poiKey)
    { _hub.CheckThread(); return _combatKeySession == _hub.CurrentSession?.Id && _combatKeys.ContainsKey((owner, localId, poiKey)); }

    /// <summary>
    /// True when a point of interest belongs to owned authored content and must therefore stay exactly as
    /// authored.
    ///
    /// On the first visit to a wormhole or jump gate that holds no persistables, the game calls
    /// <c>PoiWindowDressingHelper.AddWindowDressing</c>, which rolls a gun platform, an asteroid field,
    /// cargo containers and a derelict ship onto the point of interest. An authored rift or gate is created
    /// deliberately empty, which is exactly the condition that qualifies it for that dressing, so an owned
    /// door would otherwise collect random wrecks, rocks and stations it never declared. Owned content is
    /// author-placed only, so the dressing is skipped for it; every other point of interest keeps vanilla
    /// behaviour.
    /// </summary>
    internal bool OwnsUndressedPoi(string? poiId, string? systemId)
    {
        _hub.CheckThread();
        if (_wormholeCoordinator?.OwnsWormholePoi(poiId) == true) return true;
        if (_authoredCoordinator == null) return false;
        return _authoredCoordinator.OwnsGatePoi(poiId) || _authoredCoordinator.OwnsSystem(systemId);
    }

    private readonly MooredShipRegistry? _shipDefinitions;
    private readonly MooredShipCoordinator? _shipCoordinator;
    private readonly VGModAPI.Core.Integration.IEncounterNative? _encounters;
    private event Action<Guid>? _shipsSettled;
    private readonly bool _ownsAmbient, _ownsProtection, _ownsDroneBays;
    private readonly List<Action<Guid>> _authoredRefreshes = new();
    private readonly List<Action<string, (string Owner, string LocalId, string PoiKey)[]>> _removeNotifiers = new();
    private event Action<ReconstructionSettledEvent>? _authoredSettled;
    public IAmbientTrafficService AmbientTraffic { get { _hub.CheckThread(); return _ambient; } }
    public IUnitProtectionService UnitProtection { get { _hub.CheckThread(); return _protection; } }
    public IDroneBayService DroneBays { get { _hub.CheckThread(); return _droneBays; } }
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    private bool _disposed;
    internal WorldContentService(LifecycleHub hub, WorldDefinitionRegistry definitions, WorldAuthoringGate authoring, Func<bool> canAuthor, Action? providerReleased = null, AmbientTrafficService? ambient = null, UnitProtectionService? protection = null, DroneBayService? droneBays = null, PocketSystemRegistry? authoredDefinitions = null, PocketSystemCoordinator? authoredCoordinator = null, ResourceSiteRegistry? siteDefinitions = null, ResourceSiteCoordinator? siteCoordinator = null, MooredShipRegistry? shipDefinitions = null, MooredShipCoordinator? shipCoordinator = null, VGModAPI.Core.Integration.IEncounterNative? encounters = null, WormholePairRegistry? wormholeDefinitions = null, WormholePairCoordinator? wormholeCoordinator = null)
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
    /// refreshes every provider's owned poi objects so their Changed events fire on their own
    /// transitions (e.g. Pending → Reconstructed once native construction surfaces the pocket).
    /// </summary>
    internal void MaintainPocketSystems(Guid session)
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
        CompletePendingRemovals();
    }

    /// <summary>Sweep for <c>RequestRemoval</c> pois: completes a deferred removal once the world
    /// is safely actionable and the poi reports <see cref="RemovalStatus.Ready"/>,
    /// mirroring the game's ambient cleanup window. A request persists until its conditions clear;
    /// it is evicted when the removal completes, and evicted (abandoned) when the owning session is
    /// replaced/ends, because a handle bound to an ended session can never act again.
    private readonly HashSet<PendingRemoval> _pendingRemovals = new();
    /// <summary>A deferred-removal request bound to the session it was queued in.</summary>
    private readonly struct PendingRemoval : IEquatable<PendingRemoval>
    {
        public Guid Session { get; }
        public Func<bool> Finalize { get; }
        public PendingRemoval(Guid session, Func<bool> finalize) { Session = session; Finalize = finalize; }
        public bool Equals(PendingRemoval other) => ReferenceEquals(Finalize, other.Finalize);
        public override bool Equals(object? o) => o is PendingRemoval p && Equals(p);
        public override int GetHashCode() => Finalize.GetHashCode();
    }
    internal int PendingRemovalCount => _pendingRemovals.Count;
    private void CompletePendingRemovals()
    {
        if (_disposed) return;
        var current = _hub.CurrentSession?.Id ?? Guid.Empty;
        foreach (var pending in _pendingRemovals.ToArray())
        {
            // A request queued in a session that is no longer current is abandoned: the handle bound
            // to that session can never act, so evict it instead of leaking it forever.
            if (pending.Session != Guid.Empty && pending.Session != current)
            { _pendingRemovals.Remove(pending); continue; }
            try { if (pending.Finalize()) _pendingRemovals.Remove(pending); } catch { /* one request's fault must not block the others */ }
        }
    }
    internal void RegisterPendingRemoval(Guid session, Func<bool> finalize)
    { if (finalize != null) _pendingRemovals.Add(new PendingRemoval(session, finalize)); }
    /// <summary>
    /// After a successful native pocket removal: drop the retained authored-site rows inside the
    /// removed system (their native POIs were removed with it), drop the authored dungeon pois that
    /// were attached to those sites and captured before the removal, and let every provider terminally
    /// mark and release its owned poi objects for that pocket.
    /// </summary>
    private void PocketRemoved(string systemId, Guid[] attachedDungeons)
    {
        var droppedSites = _siteCoordinator?.DropPoisInSystem(systemId) ?? Array.Empty<(string, string, string)>();
        _siteCoordinator?.RemoveAttachedDungeons(attachedDungeons);
        foreach (var notify in _removeNotifiers.ToArray())
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
        PocketSystemRegistry.Provider? authored = null;
        if (_authoredDefinitions != null)
        {
            authored = _authoredDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (authored == null) { provider.Dispose(); return null; }
        }
        WormholePairRegistry.Provider? wormholes = null;
        if (_wormholeDefinitions != null)
        {
            wormholes = _wormholeDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (wormholes == null) { provider.Dispose(); authored?.Dispose(); return null; }
        }
        ResourceSiteRegistry.Provider? sites = null;
        if (_siteDefinitions != null)
        {
            sites = _siteDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (sites == null) { provider.Dispose(); authored?.Dispose(); wormholes?.Dispose(); return null; }
        }
        MooredShipRegistry.Provider? ships = null;
        if (_shipDefinitions != null)
        {
            ships = _shipDefinitions.Acquire(pluginInstance, Assembly.GetCallingAssembly());
            if (ships == null) { provider.Dispose(); authored?.Dispose(); wormholes?.Dispose(); sites?.Dispose(); return null; }
        }
        return new Provider(this, provider, authored, sites, ships, wormholes);
    }
    private sealed class Provider : IWorldProvider, IWorldProviderEngine
    {
        private readonly Dictionary<(string LocalId, string PoiKey), CombatSiteHandle> _sites = new();
        private readonly IDisposable _siteSubscription;
        private readonly WorldContentService _service;
        private readonly WorldDefinitionRegistry.Provider _provider;
        private readonly PocketSystemRegistry.Provider? _authored;
        private readonly ResourceSiteRegistry.Provider? _authoredSites;
        private readonly WormholePairRegistry.Provider? _wormholes;
        private readonly Dictionary<(string LocalId, string PoiKey), WormholePairHandle> _wormholeObjects = new();
        private readonly MooredShipRegistry.Provider? _authoredShips;
        private readonly Dictionary<(string LocalId, string PoiKey), MooredShipHandle> _shipObjects = new();
        private readonly Dictionary<(string LocalId, string PoiKey), ResourceSiteHandle> _siteObjects = new();
        private readonly Dictionary<(string LocalId, string PoiKey), PocketSystemHandle> _objects = new();
        private readonly IDisposable _objectSubscription;
        private readonly Action<Guid> _refreshesEntry;
        private readonly Action<string, (string Owner, string LocalId, string PoiKey)[]> _removeEntry;
        private readonly Func<bool> _alive;
        private bool _disposed;
        internal Provider(WorldContentService service, WorldDefinitionRegistry.Provider provider, PocketSystemRegistry.Provider? authored, ResourceSiteRegistry.Provider? authoredSites = null, MooredShipRegistry.Provider? authoredShips = null, WormholePairRegistry.Provider? wormholes = null)
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
                _refreshesEntry = RefreshContentObjects;
                service._authoredRefreshes.Add(_refreshesEntry);
            }
            else
            {
                _objectSubscription = null!;
                _refreshesEntry = RefreshContentObjects;
                service._authoredRefreshes.Add(_refreshesEntry);
            }
            _removeEntry = OnPocketRemoved;
            service._removeNotifiers.Add(_removeEntry);
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
        /// <summary>Terminally marks and releases this provider's site objects dropped with a removed pocket.</summary>
        private void OnPocketRemoved(string systemId, (string Owner, string LocalId, string PoiKey)[] droppedSites)
        {
            if (_disposed || _authoredSites == null) return;
            foreach (var dropped in droppedSites)
            {
                if (dropped.Owner != _authoredSites.Owner) continue;
                if (_siteObjects.TryGetValue((dropped.LocalId, dropped.PoiKey), out var handle))
                {
                    _siteObjects.Remove((dropped.LocalId, dropped.PoiKey));
                    handle.MarkRemoved();
                }
            }
        }
        public event Action<PocketSystemsSettledEvent>? PocketSystemReconstructionSettled;
        private void ForwardSettled(ReconstructionSettledEvent args)
        {
            if (_authored == null || _service._authoredCoordinator == null) return;
            if (_service._hub.CurrentSession?.Id != args.SessionId) return;
            // Failures come from the coordinator's own settle classification (it maps Pending → NativeMissing
            // or PersistenceUnavailable, which the raw object State does not carry). Inflate the owned objects.
            var failures = new List<ReconstructionFailure>();
            var failedKeys = new System.Collections.Generic.HashSet<(string Local, string Key)>();
            foreach (var failure in args.Failures)
            {
                var reference = failure.Reference;
                if (reference == null || reference.ProviderId != _authored.Owner) continue;
                var handle = ObtainHandle(reference.LocalId, reference.PoiKey, args.SessionId);
                handle.Refresh();
                failures.Add(new ReconstructionFailure(handle, failure.Reason));
                failedKeys.Add((reference.LocalId, reference.PoiKey));
            }
            var reconstructed = new List<IPocketSystem>();
            foreach (var row in _service._authoredCoordinator.Pois(_authored.Owner))
            {
                if (failedKeys.Contains((row.LocalId, row.PoiKey))) continue;
                var handle = ObtainHandle(row.LocalId, row.PoiKey, args.SessionId);
                handle.Refresh();   // Changed fires for each transitioned poi
                if (handle.State.Reconstructed) reconstructed.Add(handle);
            }
            // Deliver to every consumer handler independently: one faulty handler must not starve the
            // rest of this provider's subscribers of the session's only aggregate reconstruction event.
            var subscribers = PocketSystemReconstructionSettled;
            if (subscribers == null) return;
            var settledEvent = new PocketSystemsSettledEvent(args.SessionId, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                try { ((Action<PocketSystemsSettledEvent>)subscriber)(settledEvent); }
                catch { /* fail-open per subscriber */ }
            }
        }
        public string ProviderId => _provider.Owner;
        public WorldContentStatus RegisterCombatSite(CombatSiteDefinition definition, CombatSiteDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldContentStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldContentStatus.NotReady;
            if (definition == null) return WorldContentStatus.InvalidDefinition;
            try
            {
                var native = new WorldCombatDefinition(definition.LocalId, definition.Revision, definition.Name, definition.FactionId, definition.Level);
                if (_service._definitions.TryResolve(_provider, native.LocalId, out _)) return WorldContentStatus.DuplicateDefinition;
                var prior = previous == null ? null : new WorldCombatDefinition(previous.LocalId, previous.Revision, previous.Name, previous.FactionId, previous.Level);
                return _provider.Register(native, prior) ? WorldContentStatus.Succeeded : WorldContentStatus.Rejected;
            }
            catch (ArgumentException) { return WorldContentStatus.InvalidDefinition; }
        }
        /// <summary>Uniform poi-key contract: bounded, no control characters.</summary>
        internal static bool ValidPoiKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key) || key!.Length > 128) return false;
            foreach (char character in key) if (char.IsControl(character)) return false;
            return true;
        }

        /// <summary>Deterministic API-allocated native identity for an author-local poi key.</summary>
        internal static Guid SiteInstanceId(string providerId, string localId, string poiKey)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(providerId + "\n" + localId + "\n" + poiKey));
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
                var handle = ObtainSite(row.LocalId, row.PoiKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new CombatSiteFailure(handle, state.Reason ?? ReconstructionFailureReason.NativeMissing));
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
                    var handle = ObtainSite(row.LocalId, row.PoiKey, session.Id);
                    handle.Refresh();
                    list.Add(handle);
                }
            return list;
        }

        public ICombatSite? CreateCombatSite(string localId, string poiKey, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidPoiKey(poiKey)) return null;
            if (OtherKindOwnsKey(exceptShips: false, localId, poiKey)) return null;
            if (!_service._definitions.TryResolve(_provider, localId, out _)) return null;
            var instanceId = SiteInstanceId(ProviderId, localId, poiKey);
            // Keyed reconciliation: an existing poi under this key is the poi; never a duplicate.
            var existing = FindPersistentCombatSite(session.Id, new CombatSiteReference(ProviderId, localId, instanceId));
            var result = existing.Succeeded ? existing : CreatePersistentCombatSite(session.Id, localId, instanceId, systemId, x, y);
            if (result.Status is not (WorldContentStatus.Succeeded or WorldContentStatus.Rejected)) return null;
            if (result.Status == WorldContentStatus.Succeeded)
            {
                _service.EnsureCombatKeySession(session.Id);
                _service._combatKeys[(ProviderId, localId, poiKey)] = new CombatSiteKeyRow(ProviderId, localId, poiKey, instanceId);
            }
            var handle = ObtainSite(localId, poiKey, session.Id);
            handle.RecordAction(result.Status == WorldContentStatus.Succeeded
                ? new WorldContentResult(WorldContentStatus.Succeeded)
                : new WorldContentResult(WorldContentStatus.Rejected, "The native site could not be created."));
            handle.Refresh();
            return handle;
        }

        public ICombatSite? GetCombatSite(string localId, string poiKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidPoiKey(poiKey)) return null;
            var instanceId = SiteInstanceId(ProviderId, localId, poiKey);
            if (!FindPersistentCombatSite(session.Id, new CombatSiteReference(ProviderId, localId, instanceId)).Succeeded) return null;
            var handle = ObtainSite(localId, poiKey, session.Id);
            handle.Refresh();
            return handle;
        }

        private CombatSiteHandle ObtainSite(string localId, string poiKey, Guid session)
        {
            var key = (localId, poiKey);
            if (_sites.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new CombatSiteHandle(this, localId, poiKey, session);
            _sites[key] = handle;
            return handle;
        }

        public CombatSiteResult FindPersistentCombatSite(Guid expectedSessionId, CombatSiteReference reference)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new CombatSiteResult(WorldContentStatus.UnknownProvider);
            if (reference == null || reference.ProviderId != ProviderId) return new CombatSiteResult(WorldContentStatus.NotRegistered);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new CombatSiteResult(WorldContentStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId) return new CombatSiteResult(WorldContentStatus.NotReady);
            try
            {
                var record = _service._authoring.TryFind(_provider, expectedSessionId, reference.LocalId, reference.InstanceId,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed);
                return record == null ? new CombatSiteResult(WorldContentStatus.NotRegistered) :
                    new CombatSiteResult(WorldContentStatus.Succeeded, new CombatSiteReference(record.Identity.Owner, record.Identity.LocalId, record.Identity.InstanceId), record.Identity.NativeId);
            }
            catch (ArgumentException) { return new CombatSiteResult(WorldContentStatus.InvalidDefinition); }
        }
        public CombatSiteResult CreatePersistentCombatSite(Guid expectedSessionId, string localId, Guid instanceId, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return new CombatSiteResult(WorldContentStatus.UnknownProvider);
            if (!_service._canAuthor() || _disposed || _service._disposed) return new CombatSiteResult(WorldContentStatus.Unavailable);
            if (expectedSessionId == Guid.Empty || _service._hub.CurrentSession?.Id != expectedSessionId ||
                _service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new CombatSiteResult(WorldContentStatus.NotReady);
            if (localId == null || !_service._definitions.TryResolve(_provider, localId, out _)) return new CombatSiteResult(WorldContentStatus.NotRegistered);
            try
            {
                var identity = new WorldObjectIdentity(new PersistentDeclaration(ProviderId, localId, PersistentKind.WorldObject, PersistenceImpact.ApiDependent), instanceId);
                var success = new CombatSiteResult(WorldContentStatus.Succeeded, new CombatSiteReference(ProviderId, localId, instanceId), identity.NativeId);
                return _service._authoring.TryCreate(_provider, expectedSessionId, localId, instanceId, systemId, x, y,
                    () => !_disposed && !_service._disposed && _service._canAuthor() && !_disposed && !_service._disposed) == null
                    ? new CombatSiteResult(WorldContentStatus.Rejected) : success;
            }
            catch (ArgumentException) { return new CombatSiteResult(WorldContentStatus.InvalidDefinition); }
        }

        /// <summary>
        /// Removes the owned combat site: verified native removal first, then the poi key is
        /// dropped so no save record reconstructs it. Leaves the key untouched on any refusal.
        /// </summary>
        internal (WorldContentStatus Status, string Detail) RemoveCombatSite(string localId, string poiKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _service._authoring == null)
                return (WorldContentStatus.Unavailable, "World authoring is unavailable.");
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return (WorldContentStatus.NotReady, "The world is not in a safely actionable state yet.");
            var rowKey = (ProviderId, localId, poiKey);
            if (!_service._combatKeys.ContainsKey(rowKey)) return (WorldContentStatus.NotRegistered, "");
            var instanceId = SiteInstanceId(ProviderId, localId, poiKey);
            try
            {
                var outcome = _service._authoring.RemoveChecked(_provider, session.Id, localId, instanceId,
                    () => !_disposed && !_service._disposed && _service._canAuthor());
                switch (outcome)
                {
                    case WorldRemoveOutcome.Removed:
                        _service._combatKeys.Remove(rowKey);
                        return (WorldContentStatus.Succeeded, "");
                    case WorldRemoveOutcome.Missing:
                        return (WorldContentStatus.Rejected, "The combat site is not currently present natively.");
                    default:
                        return (WorldContentStatus.Rejected, "The native removal could not be performed or verified.");
                }
            }
            catch (Exception error)
            { _service._hub.ReportSubscriberFailure("world.combat-remove", error); return (WorldContentStatus.Unavailable, "The native removal faulted."); }
        }

        /// <summary>
        /// Pure readiness for removing the owned combat site (no mutation): Ready, PlayerInside,
        /// NotPresent, SessionEnded, NotReady or Unavailable.
        /// </summary>
        internal RemovalStatus CanRemoveCombatSite(string localId, string poiKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _service._authoring == null) return RemovalStatus.Unavailable;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return RemovalStatus.SessionEnded;
            if (session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return RemovalStatus.NotReady;
            var rowKey = (ProviderId, localId, poiKey);
            if (!_service._combatKeys.ContainsKey(rowKey)) return RemovalStatus.NotPresent;
            var instanceId = SiteInstanceId(ProviderId, localId, poiKey);
            try
            {
                return _service._authoring.CanRemove(_provider, session.Id, localId, instanceId,
                    () => !_disposed && !_service._disposed && _service._canAuthor());
            }
            catch (Exception error)
            { _service._hub.ReportSubscriberFailure("world.combat-canremove", error); return RemovalStatus.Unavailable; }
        }

        public event Action<ResourceSitesSettledEvent>? ResourceSiteReconstructionSettled;
        private void ForwardSitesSettled(Guid session)
        {
            if (_authoredSites == null || _service._siteCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var reconstructed = new List<IResourceSite>();
            var failures = new List<ResourceSiteFailure>();
            foreach (var row in _service._siteCoordinator.Pois(_authoredSites.Owner))
            {
                var handle = ObtainSiteHandle(row.LocalId, row.PoiKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new ResourceSiteFailure(handle,
                    state.Reason ?? _service._siteCoordinator.PendingReason(session)));
            }
            var subscribers = ResourceSiteReconstructionSettled;
            if (subscribers == null) return;
            var settled = new ResourceSitesSettledEvent(session, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<ResourceSitesSettledEvent>)subscriber)(settled); } catch { /* fail-open per subscriber */ } }
        }

        public WorldContentStatus RegisterResourceSite(ResourceSiteDefinition definition, ResourceSiteDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldContentStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldContentStatus.NotReady;
            if (_authoredSites == null || _service._siteDefinitions == null || definition == null) return WorldContentStatus.InvalidDefinition;
            try
            {
                var mapped = new ResourceSiteDeclaration(definition);
                if (_service._siteDefinitions.TryResolve(_authoredSites, definition.LocalId, out _)) return WorldContentStatus.DuplicateDefinition;
                var prior = previous == null ? null : new ResourceSiteDeclaration(previous);
                return _service._siteDefinitions.Register(_authoredSites, mapped, prior) ? WorldContentStatus.Succeeded : WorldContentStatus.Rejected;
            }
            catch (ArgumentException) { return WorldContentStatus.InvalidDefinition; }
        }

        public IResourceSite? CreateResourceSite(string localId, string poiKey, string systemId, float x, float y)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return null;
            if (!_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidPoiKey(poiKey)) return null;
            // The persistence envelope keys pois per (owner, local, key) across ALL kinds; a
            // cross-kind collision must be refused here, not discovered at save time.
            if (_authored != null && _service._authoredCoordinator != null
                && _service._authoredCoordinator.ContainsPoi(_authored.Owner, localId, poiKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsUnit(_authoredShips.Owner, localId, poiKey)) return null;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, poiKey)) return null;
            if (CombatKeyOwnsKey(localId, poiKey)) return null;
            var (status, _) = _service._siteCoordinator.Create(_authoredSites, session.Id, localId, poiKey, systemId, x, y);
            if (status != WorldContentStatus.Succeeded && status != WorldContentStatus.Rejected) return null;
            if (_service._siteCoordinator.TryGetPoi(_authoredSites.Owner, localId, poiKey) == null && status != WorldContentStatus.Rejected) return null;
            var handle = ObtainSiteHandle(localId, poiKey, session.Id);
            handle.RecordAction(status == WorldContentStatus.Succeeded
                ? new WorldContentResult(WorldContentStatus.Succeeded)
                : new WorldContentResult(WorldContentStatus.Rejected, "The native site could not be created."));
            handle.Refresh();
            return handle;
        }

        public IResourceSite? GetResourceSite(string localId, string poiKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidPoiKey(poiKey)) return null;
            if (_service._siteCoordinator.TryGetPoi(_authoredSites.Owner, localId, poiKey) == null) return null;
            var handle = ObtainSiteHandle(localId, poiKey, session.Id);
            handle.Refresh();
            return handle;
        }

        public IReadOnlyList<IResourceSite> GetResourceSites(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredSites == null || _service._siteCoordinator == null) return Array.Empty<IResourceSite>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IResourceSite>();
            var list = new List<IResourceSite>();
            foreach (var row in _service._siteCoordinator.Pois(_authoredSites.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainSiteHandle(row.LocalId, row.PoiKey, session.Id));
            return list;
        }

        private ResourceSiteHandle ObtainSiteHandle(string localId, string poiKey, Guid session)
        {
            var key = (localId, poiKey);
            if (_siteObjects.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new ResourceSiteHandle(this, localId, poiKey, session);
            _siteObjects[key] = handle;
            return handle;
        }

        public event Action<MooredShipsSettledEvent>? MooredShipReconstructionSettled;
        private void ForwardShipsSettled(Guid session)
        {
            if (_authoredShips == null || _service._shipCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var reconstructed = new List<IMooredShip>();
            var failures = new List<MooredShipFailure>();
            foreach (var row in _service._shipCoordinator.Units(_authoredShips.Owner))
            {
                var handle = ObtainShipHandle(row.LocalId, row.UnitKey, session);
                handle.Refresh();
                var state = handle.State;
                if (state.Reconstructed) reconstructed.Add(handle);
                else failures.Add(new MooredShipFailure(handle, state.Reason ?? _service._shipCoordinator.PendingReason(session)));
            }
            var subscribers = MooredShipReconstructionSettled;
            if (subscribers == null) return;
            var settled = new MooredShipsSettledEvent(session, reconstructed, failures);
            foreach (var subscriber in subscribers.GetInvocationList())
            { try { ((Action<MooredShipsSettledEvent>)subscriber)(settled); } catch { /* fail-open per subscriber */ } }
        }

        public WorldContentStatus RegisterMooredShip(MooredShipDefinition definition, MooredShipDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldContentStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldContentStatus.NotReady;
            if (_authoredShips == null || _service._shipDefinitions == null || definition == null) return WorldContentStatus.InvalidDefinition;
            try
            {
                var mapped = new MooredShipDeclaration(definition);
                if (_service._shipDefinitions.TryResolve(_authoredShips, definition.LocalId, out _)) return WorldContentStatus.DuplicateDefinition;
                var prior = previous == null ? null : new MooredShipDeclaration(previous);
                return _service._shipDefinitions.Register(_authoredShips, mapped, prior) ? WorldContentStatus.Succeeded : WorldContentStatus.Rejected;
            }
            catch (ArgumentException) { return WorldContentStatus.InvalidDefinition; }
        }

        public IMooredShip? CreateMooredShip(string localId, string unitKey, string stationPoiId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return null;
            if (!_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidPoiKey(unitKey)) return null;
            // The persistence envelope keys pois per (owner, local, key) across ALL kinds.
            if (OtherKindOwnsKey(exceptShips: true, localId, unitKey) || CombatKeyOwnsKey(localId, unitKey)) return null;
            var (status, _) = _service._shipCoordinator.Create(_authoredShips, session.Id, localId, unitKey, stationPoiId);
            if (status != WorldContentStatus.Succeeded && status != WorldContentStatus.Rejected) return null;
            var handle = ObtainShipHandle(localId, unitKey, session.Id);
            handle.RecordAction(status == WorldContentStatus.Succeeded
                ? new WorldContentResult(WorldContentStatus.Succeeded)
                : new WorldContentResult(WorldContentStatus.Rejected, "The moored ship could not be created."));
            handle.Refresh();
            return handle;
        }

        public IMooredShip? GetMooredShip(string localId, string unitKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || localId == null || !ValidPoiKey(unitKey)) return null;
            if (_service._shipCoordinator.TryGetUnit(_authoredShips.Owner, localId, unitKey) == null) return null;
            var handle = ObtainShipHandle(localId, unitKey, session.Id);
            handle.Refresh();
            return handle;
        }

        private bool OtherKindOwnsKey(bool exceptShips, string localId, string unitKey)
        {
            if (_authored != null && _service._authoredCoordinator != null
                && _service._authoredCoordinator.ContainsPoi(_authored.Owner, localId, unitKey)) return true;
            if (_authoredSites != null && _service._siteCoordinator != null
                && _service._siteCoordinator.ContainsPoi(_authoredSites.Owner, localId, unitKey)) return true;
            if (!exceptShips && _authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsUnit(_authoredShips.Owner, localId, unitKey)) return true;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, unitKey)) return true;
            return false;
        }
        private bool CombatKeyOwnsKey(string localId, string unitKey)
        {
            return _service.CombatKeyClaimed(ProviderId, localId, unitKey);
        }

        public IReadOnlyList<IMooredShip> GetMooredShips(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authoredShips == null || _service._shipCoordinator == null) return Array.Empty<IMooredShip>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IMooredShip>();
            var list = new List<IMooredShip>();
            foreach (var row in _service._shipCoordinator.Units(_authoredShips.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainShipHandle(row.LocalId, row.UnitKey, session.Id));
            return list;
        }

        private MooredShipHandle ObtainShipHandle(string localId, string unitKey, Guid session)
        {
            var key = (localId, unitKey);
            if (_shipObjects.TryGetValue(key, out var existing) && existing.Session == session) return existing;
            var handle = new MooredShipHandle(this, localId, unitKey, session);
            _shipObjects[key] = handle;
            return handle;
        }

        /// <summary>The owned moored-ship poi object; one poi per key per session.</summary>
        private sealed class MooredShipHandle : IMooredShip
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _unitKey;
            internal readonly Guid Session;
            private MooredShipState _state = new(ReconstructionStatus.Pending);
            private WorldContentResult _lastAction = new(WorldContentStatus.NotReady, "No action has been taken yet on this poi.");
            private event Action<IMooredShip>? _changed;
            internal MooredShipHandle(Provider provider, string localId, string unitKey, Guid session)
            { _provider = provider; _localId = localId; _unitKey = unitKey; Session = session; }
            public string UnitKey => _unitKey;
            public MooredShipDefinition Definition
            {
                get
                {
                    if (_provider._service._shipDefinitions != null && _provider._authoredShips != null
                        && _provider._service._shipDefinitions.TryResolve(_provider._authoredShips, _localId, out var declaration) && declaration != null)
                        return declaration.ToDefinition();
                    var row = _provider._service._shipCoordinator?.TryGetUnit(_provider._authoredShips?.Owner ?? "", _localId, _unitKey);
                    return new MooredShipDefinition(_localId, row?.Revision ?? 1, "unknown", "unknown", "unknown", 0, 0);
                }
            }
            public MooredShipState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? UnitId => State.UnitId;
            public WorldContentResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<IMooredShip>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(WorldContentResult result) => _lastAction = result;
            internal void Refresh()
            {
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || _provider._authoredShips == null || _provider._service._shipCoordinator == null) return;
                var updated = _provider._service._shipCoordinator.ReconstructionState(_provider._authoredShips.Owner, _localId, _unitKey);
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
                return new EncounterSpawnResult(WorldContentStatus.Unavailable, detail: "Encounter integration is unavailable.");
            if (!_service._canAuthor()) return new EncounterSpawnResult(WorldContentStatus.Unavailable, detail: "World authoring is unavailable.");
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                return new EncounterSpawnResult(WorldContentStatus.NotReady, detail: "The world is not in a safely actionable state yet.");
            if (string.IsNullOrWhiteSpace(poiId) || poiId.Length > 4096)
                return new EncounterSpawnResult(WorldContentStatus.Rejected, detail: "A POI identity is required.");
            var outcome = _service._encounters.Spawn(session.Id, poiId, composition);
            if (outcome == null) return new EncounterSpawnResult(WorldContentStatus.Unavailable, detail: "The encounter could not be scheduled.");
            var (scheduled, detail) = outcome.Value;
            int requested = 0;
            foreach (var wave in composition.Waves) requested += wave.Count;
            return scheduled == requested
                ? new EncounterSpawnResult(WorldContentStatus.Succeeded, scheduled)
                : new EncounterSpawnResult(WorldContentStatus.Rejected, scheduled,
                    detail.Length > 0 ? detail : "The native trigger scheduled a different unit count than authored.");
        }

        public event Action<WormholePairsSettledEvent>? WormholePairReconstructionSettled;
        private void ForwardWormholesSettled(Guid session)
        {
            if (_wormholes == null || _service._wormholeCoordinator == null || _service._hub.CurrentSession?.Id != session) return;
            var good = new List<IWormholePair>(); var failed = new List<IWormholePair>();
            foreach (var row in _service._wormholeCoordinator.Pois(_wormholes.Owner))
            {
                var handle = ObtainWormhole(row.LocalId, row.PoiKey, session); handle.Refresh();
                if (handle.State.Reconstructed) good.Add(handle); else failed.Add(handle);
            }
            var subscribers = WormholePairReconstructionSettled; if (subscribers == null) return;
            var args = new WormholePairsSettledEvent(session, good, failed);
            foreach (var subscriber in subscribers.GetInvocationList()) try { ((Action<WormholePairsSettledEvent>)subscriber)(args); } catch { }
        }
        public WorldContentStatus RegisterWormholePair(WormholePairDefinition definition, WormholePairDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldContentStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldContentStatus.NotReady;
            if (_wormholes == null || definition == null) return WorldContentStatus.InvalidDefinition;
            try
            {
                if (_service._wormholeDefinitions!.TryResolve(_wormholes, definition.LocalId, out _)) return WorldContentStatus.DuplicateDefinition;
                return _wormholes.Register(definition, previous) ? WorldContentStatus.Succeeded : WorldContentStatus.Rejected;
            }
            catch (ArgumentException) { return WorldContentStatus.InvalidDefinition; }
        }
        public IWormholePair? CreateWormholePair(string localId, string poiKey, string firstSystemId, string secondSystemId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _wormholes == null || _service._wormholeCoordinator == null || !_service._canAuthor()) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidPoiKey(poiKey)) return null;
            if (_authored != null && _service._authoredCoordinator != null && _service._authoredCoordinator.ContainsPoi(_authored.Owner, localId, poiKey)) return null;
            if (_authoredSites != null && _service._siteCoordinator != null && _service._siteCoordinator.ContainsPoi(_authoredSites.Owner, localId, poiKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null && _service._shipCoordinator.ContainsUnit(_authoredShips.Owner, localId, poiKey)) return null;
            if (CombatKeyOwnsKey(localId, poiKey)) return null;
            var result = _service._wormholeCoordinator.Create(_wormholes, session.Id, localId, poiKey, firstSystemId, secondSystemId);
            return result.Row == null ? null : ObtainWormhole(localId, poiKey, session.Id);
        }
        public IWormholePair? GetWormholePair(string localId, string poiKey)
        {
            _service._hub.CheckThread(); if (_wormholes == null || _service._wormholeCoordinator?.TryGet(_wormholes.Owner, localId, poiKey) == null) return null;
            var session = _service._hub.CurrentSession; return session == null ? null : ObtainWormhole(localId, poiKey, session.Id);
        }
        public IReadOnlyList<IWormholePair> GetWormholePairs(string localId)
        {
            _service._hub.CheckThread(); if (_wormholes == null || _service._wormholeCoordinator == null || _service._hub.CurrentSession is not { } session) return Array.Empty<IWormholePair>();
            return _service._wormholeCoordinator.Pois(_wormholes.Owner).Where(r => r.LocalId == localId).Select(r => (IWormholePair)ObtainWormhole(r.LocalId, r.PoiKey, session.Id)).ToArray();
        }
        private WormholePairHandle ObtainWormhole(string localId, string poiKey, Guid session)
        {
            var key = (localId, poiKey); if (_wormholeObjects.TryGetValue(key, out var found)) return found;
            var handle = new WormholePairHandle(this, localId, poiKey, session, () => _wormholeObjects.Remove(key)); _wormholeObjects.Add(key, handle); handle.Refresh(); return handle;
        }

        public WorldContentStatus RegisterPocketSystem(PocketSystemDefinition definition, PocketSystemDefinition? previous = null)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed) return WorldContentStatus.UnknownProvider;
            if (_service._hub.CurrentSession != null) return WorldContentStatus.NotReady;
            if (_authored == null || definition == null) return WorldContentStatus.InvalidDefinition;
            try
            {
                if (_service._authoredDefinitions!.TryResolve(_authored, definition.LocalId, out _)) return WorldContentStatus.DuplicateDefinition;
                return _authored.Register(definition, previous) ? WorldContentStatus.Succeeded : WorldContentStatus.Rejected;
            }
            catch (ArgumentException) { return WorldContentStatus.InvalidDefinition; }
        }
        public IPocketSystem? CreatePocketSystem(string localId, string poiKey, string anchorSystemId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return null;
            if (!_service._canAuthor() || _disposed || _service._disposed) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks) return null;
            if (localId == null || !ValidPoiKey(poiKey)) return null;
            // The persistence envelope keys pois per (owner, local, key) across ALL kinds.
            if (_authoredSites != null && _service._siteCoordinator != null
                && _service._siteCoordinator.ContainsPoi(_authoredSites.Owner, localId, poiKey)) return null;
            if (_authoredShips != null && _service._shipCoordinator != null
                && _service._shipCoordinator.ContainsUnit(_authoredShips.Owner, localId, poiKey)) return null;
            if (_wormholes != null && _service._wormholeCoordinator != null
                && _service._wormholeCoordinator.Contains(_wormholes.Owner, localId, poiKey)) return null;
            if (CombatKeyOwnsKey(localId, poiKey)) return null;
            var result = _service._authoredCoordinator.Create(_authored, session.Id, localId, poiKey, anchorSystemId);
            if (result.Status != WorldContentStatus.Succeeded && result.Status != WorldContentStatus.Rejected) return null;
            if (!_service._authoredCoordinator.ContainsPoi(_authored.Owner, localId, poiKey)) return null;
            return ObtainHandle(localId, poiKey, session.Id);
        }
        public IReadOnlyList<IPocketSystem> GetPocketSystems(string localId)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return Array.Empty<IPocketSystem>();
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return Array.Empty<IPocketSystem>();
            var list = new List<IPocketSystem>();
            foreach (var row in _service._authoredCoordinator.Pois(_authored.Owner))
                if (string.Equals(row.LocalId, localId, StringComparison.Ordinal))
                    list.Add(ObtainHandle(row.LocalId, row.PoiKey, session.Id));
            return list;
        }
        public IPocketSystem? GetPocketSystem(string localId, string poiKey)
        {
            _service._hub.CheckThread();
            if (_disposed || _service._disposed || _authored == null || _service._authoredCoordinator == null) return null;
            var session = _service._hub.CurrentSession;
            if (session == null || session.Id == Guid.Empty) return null;
            if (!_service._authoredCoordinator.ContainsPoi(_authored.Owner, localId, poiKey)) return null;
            return ObtainHandle(localId, poiKey, session.Id);
        }
        private PocketSystemHandle ObtainHandle(string localId, string poiKey, Guid session)
        {
            var key = (localId, poiKey);
            if (_objects.TryGetValue(key, out var existing)) return existing;
            var handle = new PocketSystemHandle(_service, _authored!, _service._authoredCoordinator!, _alive, localId, poiKey, session,
                () => _objects.Remove(key));
            _objects[key] = handle;
            handle.Refresh();   // seed the cached state without firing Changed
            return handle;
        }
        private void RefreshContentObjects(Guid session)
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
            _service._removeNotifiers.Remove(_removeEntry);
            _siteSubscription.Dispose();
            _sites.Clear();
            _siteObjects.Clear();
            _shipObjects.Clear();
            _wormholeObjects.Clear();
            _objects.Clear();
            _provider.Dispose(); _authored?.Dispose(); _authoredSites?.Dispose(); _authoredShips?.Dispose(); _wormholes?.Dispose(); _disposed = true;
            _service._providerReleased?.Invoke();
        }

        /// <summary>The owned authored-site poi object; one poi per key per session.</summary>
        private sealed class ResourceSiteHandle : IResourceSite
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _poiKey;
            internal readonly Guid Session;
            private ResourceSiteState _state = new(ReconstructionStatus.Pending);
            private WorldContentResult _lastAction = new(WorldContentStatus.NotReady, "No action has been taken yet on this poi.");
            private event Action<IResourceSite>? _changed;
            internal ResourceSiteHandle(Provider provider, string localId, string poiKey, Guid session)
            { _provider = provider; _localId = localId; _poiKey = poiKey; Session = session; }
            public string PoiKey => _poiKey;
            public ResourceSiteDefinition Definition
            {
                get
                {
                    if (_provider._service._siteDefinitions != null && _provider._authoredSites != null
                        && _provider._service._siteDefinitions.TryResolve(_provider._authoredSites, _localId, out var declaration) && declaration != null)
                        return declaration.ToDefinition();
                    // Honor the retained row's kind; only the declarative detail is unknown.
                    var row = _provider._service._siteCoordinator?.TryGetPoi(_provider._authoredSites?.Owner ?? "", _localId, _poiKey);
                    return row?.Kind == ResourceSiteKind.SalvageSite
                        ? ResourceSiteDefinition.Salvage(_localId, row.Revision, "unknown", 1, "unknown", "unknown")
                        : ResourceSiteDefinition.MiningField(_localId, row?.Revision ?? 1, "unknown", 1, 1);
                }
            }
            public ResourceSiteState State { get { _provider._service._hub.CheckThread(); return _state; } }
            public string? PoiId => State.PoiId;
            public WorldContentResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<IResourceSite>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(WorldContentResult result) => _lastAction = result;
            public RemovalStatus CanRemove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return RemovalStatus.NotPresent;
                var service = _provider._service;
                if (_provider._disposed || service._disposed || _provider._authoredSites == null || service._siteCoordinator == null || !service._canAuthor())
                    return RemovalStatus.Unavailable;
                if (service._hub.CurrentSession?.Id != Session) return RemovalStatus.SessionEnded;
                if (service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || service._hub.IsDispatchingCallbacks)
                    return RemovalStatus.NotReady;
                return service._siteCoordinator.CanRemove(_provider._authoredSites, Session, _localId, _poiKey);
            }
            private WorldContentResult? Gate()
            {
                var service = _provider._service;
                if (_provider._disposed || service._disposed || _provider._authoredSites == null || service._siteCoordinator == null || !service._canAuthor())
                    return _lastAction = new(WorldContentStatus.Unavailable);
                if (service._hub.CurrentSession?.Id != Session) return _lastAction = new(WorldContentStatus.GameEnded);
                if (service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || service._hub.IsDispatchingCallbacks)
                    return _lastAction = new(WorldContentStatus.NotReady);
                return null;
            }
            private void CompleteRemoval()
            {
                _provider._siteObjects.Remove((_localId, _poiKey));
                MarkRemoved();
            }
            public WorldContentResult Remove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _lastAction = new(WorldContentStatus.Rejected, "The poi was removed; create the key again for a fresh site.");
                if (Gate() is { } refused) return refused;
                var (status, detail) = _provider._service._siteCoordinator!.Remove(_provider._authoredSites!, Session, _localId, _poiKey);
                if (status != WorldContentStatus.Succeeded)
                    return _lastAction = new(status == WorldContentStatus.Unavailable ? WorldContentStatus.Unavailable : WorldContentStatus.Rejected, detail);
                CompleteRemoval();
                return _lastAction = new(WorldContentStatus.Succeeded);
            }
            public WorldContentResult RequestRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _lastAction = new(WorldContentStatus.Rejected, "The poi was removed; create the key again for a fresh site.");
                if (Gate() is { } refused) return refused;
                _removalRequested = true;
                _provider._service.RegisterPendingRemoval(Session, CompletePendingRemoval);
                return _lastAction = new(WorldContentStatus.Succeeded, "Queued for removal; it happens at the next safe cleanup window.");
            }
            private bool CompletePendingRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed || !_removalRequested || CanRemove() != RemovalStatus.Ready) return false;
                var (status, detail) = _provider._service._siteCoordinator!.Remove(_provider._authoredSites!, Session, _localId, _poiKey);
                if (status != WorldContentStatus.Succeeded) return false;
                CompleteRemoval();
                _lastAction = new(WorldContentStatus.Succeeded, "The requested removal completed at a cleanup window.");
                return true;
            }
            /// <summary>The pocket containing this site removed, or the site itself was removed; terminal for its session.</summary>
            internal void MarkRemoved()
            {
                if (_removed) return;
                _removed = true;
                bool changed = _state.Status != ReconstructionStatus.Removed;
                _state = new ResourceSiteState(ReconstructionStatus.Removed);
                if (changed) _changed?.Invoke(this);
            }
            private bool _removed;
            private bool _removalRequested;
            internal void Refresh()
            {
                if (_removed) return;
                // A replaced session freezes the last observed state; the handle never resolves against the replacement save.
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || _provider._authoredSites == null || _provider._service._siteCoordinator == null) return;
                var updated = _provider._service._siteCoordinator.ReconstructionState(_provider._authoredSites.Owner, _localId, _poiKey);
                bool changed = _state.Status != updated.Status || _state.Reason != updated.Reason || _state.PoiId != updated.PoiId;
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
        }

        /// <summary>The owned combat-site poi object; one poi per key per session.</summary>
        private sealed class CombatSiteHandle : ICombatSite
        {
            private readonly Provider _provider;
            private readonly string _localId;
            private readonly string _poiKey;
            internal readonly Guid Session;
            private CombatSiteState _state = new(ReconstructionStatus.Pending);
            private WorldContentResult _lastAction = new(WorldContentStatus.NotReady, "No action has been taken yet on this poi.");
            private event Action<ICombatSite>? _changed;
            private bool _removed;
            private bool _removalRequested;
            internal CombatSiteHandle(Provider provider, string localId, string poiKey, Guid session)
            { _provider = provider; _localId = localId; _poiKey = poiKey; Session = session; }
            public string PoiKey => _poiKey;
            public CombatSiteDefinition Definition
            {
                get
                {
                    if (_provider._service._definitions.TryResolve(_provider._provider, _localId, out var declaration) && declaration?.Definition is { } definition)
                        return new CombatSiteDefinition(definition.LocalId, definition.Revision, definition.Name, definition.FactionId, definition.Level);
                    return new CombatSiteDefinition(_localId, 1, "", "", 1);
                }
            }
            public CombatSiteState State { get { _provider._service._hub.CheckThread(); return _removed ? new CombatSiteState(ReconstructionStatus.Removed) : _state; } }
            public string? PoiId => State.PoiId;
            public WorldContentResult LastAction { get { _provider._service._hub.CheckThread(); return _lastAction; } }
            public event Action<ICombatSite>? Changed { add => _changed += value; remove => _changed -= value; }
            internal void RecordAction(WorldContentResult result) => _lastAction = result;
            public RemovalStatus CanRemove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return RemovalStatus.NotPresent;
                var service = _provider._service;
                if (_provider._disposed || service._disposed || !service._canAuthor()) return RemovalStatus.Unavailable;
                if (service._hub.CurrentSession?.Id != Session) return RemovalStatus.SessionEnded;
                if (service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || service._hub.IsDispatchingCallbacks)
                    return RemovalStatus.NotReady;
                return _provider.CanRemoveCombatSite(_localId, _poiKey);
            }
            private WorldContentResult? Gate()
            {
                var service = _provider._service;
                if (_provider._disposed || service._disposed || !service._canAuthor()) return _lastAction = new(WorldContentStatus.Unavailable);
                if (service._hub.CurrentSession?.Id != Session) return _lastAction = new(WorldContentStatus.GameEnded);
                if (service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || service._hub.IsDispatchingCallbacks)
                    return _lastAction = new(WorldContentStatus.NotReady);
                return null;
            }
            private void CompleteRemoval()
            {
                _removed = true;
                _provider._sites.Remove((_localId, _poiKey));
                _state = new CombatSiteState(ReconstructionStatus.Removed);
                _changed?.Invoke(this);
            }
            public WorldContentResult Remove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _lastAction = new(WorldContentStatus.Rejected, "The poi was removed; create the key again for a fresh site.");
                if (Gate() is { } refused) return refused;
                var (status, detail) = _provider.RemoveCombatSite(_localId, _poiKey);
                if (status != WorldContentStatus.Succeeded)
                    return _lastAction = new(status == WorldContentStatus.Unavailable ? WorldContentStatus.Unavailable : WorldContentStatus.Rejected, detail);
                CompleteRemoval();
                return _lastAction = new(WorldContentStatus.Succeeded);
            }
            public WorldContentResult RequestRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _lastAction = new(WorldContentStatus.Rejected, "The poi was removed; create the key again for a fresh site.");
                if (Gate() is { } refused) return refused;
                _removalRequested = true;
                _provider._service.RegisterPendingRemoval(Session, CompletePendingRemoval);
                return _lastAction = new(WorldContentStatus.Succeeded, "Queued for removal; it happens at the next safe cleanup window.");
            }
            private bool CompletePendingRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed || !_removalRequested || CanRemove() != RemovalStatus.Ready) return false;
                var (status, detail) = _provider.RemoveCombatSite(_localId, _poiKey);
                if (status != WorldContentStatus.Succeeded) return false;
                CompleteRemoval();
                _lastAction = new(WorldContentStatus.Succeeded, "The requested removal completed at a cleanup window.");
                return true;
            }
            internal void Refresh()
            {
                if (_removed) return;
                // A replaced session freezes the last observed state; the handle never resolves against the replacement save.
                if (Session == Guid.Empty || _provider._service._hub.CurrentSession?.Id != Session) return;
                if (_provider._disposed || _provider._service._disposed || !_provider._service._canAuthor()) return;
                var found = _provider.FindPersistentCombatSite(Session,
                    new CombatSiteReference(_provider.ProviderId, _localId, SiteInstanceId(_provider.ProviderId, _localId, _poiKey)));
                var updated = found.Succeeded
                    ? new CombatSiteState(ReconstructionStatus.Reconstructed, poiId: found.PoiId)
                    : new CombatSiteState(ReconstructionStatus.Pending);
                bool changed = _state.Status != updated.Status || _state.PoiId != updated.PoiId;
                _state = updated;
                if (changed) _changed?.Invoke(this);
            }
        }

        private sealed class WormholePairHandle : IWormholePair
        {
            private readonly Provider _provider; private readonly string _localId, _key; internal Guid Session { get; }
            private WormholePairState _state = new(ReconstructionStatus.Pending);
            private WorldContentResult _last = new(WorldContentStatus.NotReady, "No action has been taken yet on this poi.");
            private bool _removed;
            private readonly Action _evict;
            private IDisposable? _quietFirst, _quietSecond;
            private bool _removalRequested;
            private event Action<IWormholePair>? _changed;
            internal WormholePairHandle(Provider provider, string localId, string key, Guid session, Action evict)
            { _provider = provider; _localId = localId; _key = key; Session = session; _evict = evict; }
            public string PoiKey => _key;
            public WormholePairDefinition Definition
            {
                get
                {
                    if (_provider._wormholes != null && _provider._service._wormholeDefinitions != null &&
                        _provider._service._wormholeDefinitions.TryResolve(_provider._wormholes, _localId, out var d) && d != null)
                        return new(d.LocalId, d.Revision, d.Name, d.Quiet);
                    return new(_localId, 1, "unknown");
                }
            }
            public WormholePairState State { get { _provider._service._hub.CheckThread(); return _removed ? new(ReconstructionStatus.Removed) : _state; } }
            public string? FirstWormholePoiId => State.FirstWormholePoiId;
            public string? SecondWormholePoiId => State.SecondWormholePoiId;
            public WorldContentResult LastAction => _last;
            public event Action<IWormholePair>? Changed { add => _changed += value; remove => _changed -= value; }
            public WorldContentResult SetOpen(bool open)
            {
                _provider._service._hub.CheckThread();
                if (_provider._disposed || _provider._service._disposed || _provider._wormholes == null || _provider._service._wormholeCoordinator == null)
                    return _last = new(WorldContentStatus.Unavailable);
                if (_provider._service._hub.CurrentSession?.Id != Session) return _last = new(WorldContentStatus.GameEnded);
                if (_provider._service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _provider._service._hub.IsDispatchingCallbacks)
                    return _last = new(WorldContentStatus.NotReady);
                var status = _provider._service._wormholeCoordinator.SetOpen(_provider._wormholes, Session, _localId, _key, open);
                return _last = new(status == WorldContentStatus.Succeeded ? WorldContentStatus.Succeeded : status == WorldContentStatus.Unavailable ? WorldContentStatus.Unavailable : WorldContentStatus.Rejected);
            }
            public RemovalStatus CanRemove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return RemovalStatus.NotPresent;
                if (_provider._disposed || _provider._service._disposed || _provider._wormholes == null || _provider._service._wormholeCoordinator == null)
                    return RemovalStatus.Unavailable;
                if (_provider._service._hub.CurrentSession?.Id != Session) return RemovalStatus.SessionEnded;
                if (_provider._service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _provider._service._hub.IsDispatchingCallbacks)
                    return RemovalStatus.NotReady;
                return _provider._service._wormholeCoordinator.CanRemove(_provider._wormholes, Session, _localId, _key);
            }
            private WorldContentResult? Gate()
            {
                if (_provider._disposed || _provider._service._disposed || _provider._wormholes == null || _provider._service._wormholeCoordinator == null)
                    return _last = new(WorldContentStatus.Unavailable);
                if (_provider._service._hub.CurrentSession?.Id != Session) return _last = new(WorldContentStatus.GameEnded);
                if (_provider._service._hub.CurrentSession.Phase != SessionPhase.GameplayInitialized || _provider._service._hub.IsDispatchingCallbacks)
                    return _last = new(WorldContentStatus.NotReady);
                return null;
            }
            private void CompleteRemoval()
            {
                _removed = true;
                _evict();
                ReleaseQuiet();
                _state = new(ReconstructionStatus.Removed);
                _changed?.Invoke(this);
            }
            public WorldContentResult Remove()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _last = new(WorldContentStatus.Rejected, "The pair was removed; create the key again for a fresh pair.");
                if (Gate() is { } refused) return refused;
                var (status, detail) = _provider._service._wormholeCoordinator!.Remove(_provider._wormholes!, Session, _localId, _key);
                if (status != WorldContentStatus.Succeeded) return _last = new(status == WorldContentStatus.Unavailable ? WorldContentStatus.Unavailable : WorldContentStatus.Rejected, detail);
                CompleteRemoval();
                return _last = new(WorldContentStatus.Succeeded);
            }
            public WorldContentResult RequestRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed) return _last = new(WorldContentStatus.Rejected, "The pair was removed; create the key again for a fresh pair.");
                if (Gate() is { } refused) return refused;
                _removalRequested = true;
                _provider._service.RegisterPendingRemoval(Session, CompletePendingRemoval);
                return _last = new(WorldContentStatus.Succeeded, "Queued for removal; it happens at the next safe cleanup window.");
            }
            private bool CompletePendingRemoval()
            {
                _provider._service._hub.CheckThread();
                if (_removed || !_removalRequested || CanRemove() != RemovalStatus.Ready) return false;
                var (status, detail) = _provider._service._wormholeCoordinator!.Remove(_provider._wormholes!, Session, _localId, _key);
                if (status != WorldContentStatus.Succeeded) return false;
                CompleteRemoval();
                _last = new(WorldContentStatus.Succeeded, "The requested removal completed at a cleanup window.");
                return true;
            }
            internal void Refresh()
            {
                if (_removed) return;
                if (_provider._service._hub.CurrentSession?.Id != Session || _provider._wormholes == null || _provider._service._wormholeCoordinator == null) return;
                var updated = _provider._service._wormholeCoordinator.State(_provider._wormholes, _localId, _key);
                bool changed = updated.Status != _state.Status || updated.Reason != _state.Reason || updated.FirstWormholePoiId != _state.FirstWormholePoiId || updated.SecondWormholePoiId != _state.SecondWormholePoiId;
                _state = updated; if (changed) _changed?.Invoke(this);
                ApplyQuiet();
            }
            /// <summary>Declares the private-door quieting for both owned ends once their native identities
            /// are known. Idempotent: each end is declared at most once and re-resolves across reloads. A
            /// quiet pair spawns no passerby traffic and no security patrol at either end.</summary>
            private void ApplyQuiet()
            {
                try
                {
                    if (!Definition.Quiet) return;
                    var ambient = _provider._service._ambient;
                    if (ambient == null) return;
                    if (_quietFirst == null && _state.FirstWormholePoiId is { Length: > 0 } first)
                        _quietFirst = ambient.SuppressAtWormhole(first, _localId + "|" + _key + "|quiet-a");
                    if (_quietSecond == null && _state.SecondWormholePoiId is { Length: > 0 } second)
                        _quietSecond = ambient.SuppressAtWormhole(second, _localId + "|" + _key + "|quiet-b");
                }
                catch (Exception error) { _provider._service._hub.ReportSubscriberFailure("world.quiet-wormhole", error); }
            }
            private void ReleaseQuiet()
            {
                var first = _quietFirst; _quietFirst = null;
                var second = _quietSecond; _quietSecond = null;
                try { first?.Dispose(); } catch { }
                try { second?.Dispose(); } catch { }
            }
        }

        /// <summary>The owned poi object exposed to consumers; one poi per key per session.</summary>
        private sealed class PocketSystemHandle : IPocketSystem
        {
            private readonly WorldContentService _service;
            private readonly PocketSystemRegistry.Provider _authored;
            private readonly PocketSystemCoordinator _coordinator;
            private readonly Func<bool> _alive;
            private readonly string _localId;
            private readonly string _poiKey;
            private readonly Guid _session;
            private PocketSystemState _state = null!;
            private bool _seeded;
            private bool _removed;
            private bool _removalRequested;
            private IDisposable? _quiet;
            private readonly Action _evict;
            private WorldContentResult _lastAction = new(WorldContentStatus.NotReady, "No action has been taken yet on this poi.");
            private event Action<IPocketSystem>? _changed;

            internal PocketSystemHandle(WorldContentService service, PocketSystemRegistry.Provider authored,
                PocketSystemCoordinator coordinator, Func<bool> alive, string localId, string poiKey, Guid session, Action evict)
            { _service = service; _authored = authored; _coordinator = coordinator; _alive = alive;
                _localId = localId; _poiKey = poiKey; _session = session; _evict = evict; }

            public string PoiKey => _poiKey;
            public PocketSystemDefinition Definition
            {
                get
                {
                    if (_service._authoredDefinitions != null && _service._authoredDefinitions.TryResolve(_authored, _localId, out var declaration) && declaration != null)
                        return new PocketSystemDefinition(declaration.LocalId, declaration.Revision, declaration.Name, declaration.Placement, declaration.FactionId, declaration.SectorName, declaration.Quiet);
                    var revision = _coordinator.TryGetPoi(_authored.Owner, _localId, _poiKey)?.Revision ?? 1;
                    return new PocketSystemDefinition(_localId, revision, "");
                }
            }
            public PocketSystemState State
            {
                get
                {
                    if (_removed) return new PocketSystemState(ReconstructionStatus.Removed);
                    if (!_seeded && _service._hub.CurrentSession?.Id == _session && _session != Guid.Empty)
                    { try { _state = _coordinator.ReconstructionState(_authored, Reference); _seeded = true; } catch { } }
                    return _state ?? new PocketSystemState(ReconstructionStatus.Pending);
                }
            }
            public string? SystemId => _removed ? null : _state?.SystemId;
            public string? EntranceGatePoiId => _removed ? null : _state?.EntranceGatePoiId;
            public string? PocketGatePoiId => _removed ? null : _state?.PocketGatePoiId;
            public WorldContentResult LastAction => _lastAction;
            public event Action<IPocketSystem>? Changed { add => _changed += value; remove => _changed -= value; }

            private PocketSystemReference Reference => new(_authored.Owner, _localId, _poiKey);
            private bool IsCurrentSession() => _session != Guid.Empty && _service._hub.CurrentSession?.Id == _session;

            /// <summary>Uniform per-action gating shared by every poi action; null means actionable.</summary>
            private WorldContentResult? GateAction()
            {
                WorldContentResult Fail(WorldContentStatus status, string detail) => _lastAction = new WorldContentResult(status, detail);
                if (_removed) return Fail(WorldContentStatus.Rejected, "The poi was removed; create the key again for a fresh pocket.");
                if (!_alive()) return Fail(WorldContentStatus.Unavailable, "The provider lease is no longer active.");
                if (!IsCurrentSession()) return Fail(WorldContentStatus.GameEnded, "The owning session ended or was replaced; re-obtain the poi for the live game.");
                if (!_service._canAuthor()) return Fail(WorldContentStatus.Unavailable, "World authoring is unavailable.");
                var session = _service._hub.CurrentSession;
                if (session == null || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                    return Fail(WorldContentStatus.NotReady, "The world is not in a safely actionable state yet.");
                if (_service._authoredCoordinator == null) return Fail(WorldContentStatus.Unavailable, "Resource systems are unavailable.");
                return null;
            }

            public WorldContentResult SetEntranceOpen(bool open)
            {
                _service._hub.CheckThread();
                if (GateAction() is { } refused) return refused;
                var status = _service._authoredCoordinator!.SetOpen(_authored, _session, Reference, open);
                return _lastAction = new WorldContentResult(ToActionStatus(status), "");
            }

            public RemovalStatus CanRemove()
            {
                _service._hub.CheckThread();
                if (_removed) return RemovalStatus.NotPresent;
                if (!_alive()) return RemovalStatus.Unavailable;
                if (!IsCurrentSession()) return RemovalStatus.SessionEnded;
                if (!_service._canAuthor()) return RemovalStatus.Unavailable;
                var session = _service._hub.CurrentSession;
                if (session == null || session.Phase != SessionPhase.GameplayInitialized || _service._hub.IsDispatchingCallbacks)
                    return RemovalStatus.NotReady;
                if (_service._authoredCoordinator == null) return RemovalStatus.Unavailable;
                var row = _service._authoredCoordinator.TryGetPoi(_authored.Owner, _localId, _poiKey);
                if (row != null && _service._authoring != null)
                {
                    var contains = _service._authoring.AnyInSystem(row.SystemId);
                    if (contains == null) return RemovalStatus.NotReady;
                    if (contains == true) return RemovalStatus.CombatSitesPresent;
                }
                if (row != null && _service._wormholeCoordinator != null && _service._wormholeCoordinator.AnyPoiInSystem(row.SystemId))
                    return RemovalStatus.WormholeEndpoint;
                if (row == null) return RemovalStatus.NotPresent;
                // A pure read: an attached authored dungeon that cannot be read cannot be dropped later.
                if (CaptureAttachedDungeons(row) == null) return RemovalStatus.Unavailable;
                return _service._authoredCoordinator.CanRemove(_authored, _session, Reference);
            }
            private void CompleteRemoval(string? systemId, Guid[] attachedDungeons)
            {
                _removed = true;
                _evict();
                ReleaseQuiet();
                if (systemId != null) _service.PocketRemoved(systemId, attachedDungeons);
            }
            /// <summary>
            /// Resolves the dungeon pois attached to sites inside this pocket while its native POIs still
            /// exist. Null means a read faulted and the removal must be refused instead of leaking rows.
            /// </summary>
            private Guid[]? CaptureAttachedDungeons(PocketSystemPoi? row)
                => row == null || _service._siteCoordinator == null
                    ? Array.Empty<Guid>()
                    : _service._siteCoordinator.GetAttachedDungeonsInSystem(row.SystemId);
            private bool CompletePendingRemoval()
            {
                _service._hub.CheckThread();
                if (_removed || !_removalRequested || CanRemove() != RemovalStatus.Ready) return false;
                var captured = CaptureAttachedDungeons(_service._authoredCoordinator!.TryGetPoi(_authored.Owner, _localId, _poiKey));
                if (captured == null) return false;
                var (status, detail, systemId) = _service._authoredCoordinator!.Remove(_authored, _session, Reference);
                if (status != WorldContentStatus.Succeeded) return false;
                CompleteRemoval(systemId, captured);
                _lastAction = new WorldContentResult(WorldContentStatus.Succeeded, "The requested removal completed at a cleanup window.");
                return true;
            }
            public WorldContentResult RequestRemoval()
            {
                _service._hub.CheckThread();
                if (GateAction() is { } refused) return refused;
                _removalRequested = true;
                _service.RegisterPendingRemoval(_session, CompletePendingRemoval);
                return _lastAction = new WorldContentResult(WorldContentStatus.Succeeded, "Queued for removal; it happens at the next safe cleanup window.");
            }
            public WorldContentResult Remove()
            {
                _service._hub.CheckThread();
                if (GateAction() is { } refused) return refused;
                // Never orphan combat-site records: their removal is not supported, so their presence refuses removal.
                var row = _service._authoredCoordinator!.TryGetPoi(_authored.Owner, _localId, _poiKey);
                if (row != null && _service._authoring != null)
                {
                    var contains = _service._authoring.AnyInSystem(row.SystemId);
                    if (contains == null)
                        return _lastAction = new WorldContentResult(WorldContentStatus.NotReady,
                            "Combat-site save data is not ready to prove the pocket is empty of combat sites.");
                    if (contains == true)
                        return _lastAction = new WorldContentResult(WorldContentStatus.Rejected,
                            "The pocket still contains combat sites; they cannot be removed with it.");
                }
                // Never orphan wormhole-pair pois: a wormhole with an endpoint inside the pocket would lose
                // its pocket-side POI on remove and leave a permanently-failed persisted row, so its presence refuses removal.
                if (row != null && _service._wormholeCoordinator != null
                    && _service._wormholeCoordinator.AnyPoiInSystem(row.SystemId))
                    return _lastAction = new WorldContentResult(WorldContentStatus.Rejected,
                        "The pocket is still the endpoint of a wormhole; remove the wormhole before removing the pocket.");
                // Capture the attached authored dungeon pois before the pocket takes its sites with it.
                var captured = CaptureAttachedDungeons(row);
                if (captured == null)
                    return _lastAction = new WorldContentResult(WorldContentStatus.Unavailable,
                        "The attached dungeon state of a site inside the pocket is unavailable.");
                var (status, detail, systemId) = _service._authoredCoordinator.Remove(_authored, _session, Reference);
                if (status != WorldContentStatus.Succeeded) return _lastAction = new WorldContentResult(ToActionStatus(status), detail);
                CompleteRemoval(systemId, captured);
                return _lastAction = new WorldContentResult(WorldContentStatus.Succeeded);
            }
            private static WorldContentStatus ToActionStatus(WorldContentStatus status) => status switch
            {
                WorldContentStatus.Succeeded => WorldContentStatus.Succeeded,
                WorldContentStatus.NotReady => WorldContentStatus.NotReady,
                WorldContentStatus.Rejected or WorldContentStatus.NotRegistered or WorldContentStatus.InvalidDefinition or WorldContentStatus.DuplicateDefinition => WorldContentStatus.Rejected,
                _ => WorldContentStatus.Unavailable
            };

            internal void Refresh()
            {
                if (_removed) return;
                if (_service._hub.CurrentSession?.Id != _session || _session == Guid.Empty) return;
                PocketSystemState updated;
                try { updated = _coordinator.ReconstructionState(_authored, Reference); }
                catch { return; }
                if (!_seeded) { _state = updated; _seeded = true; ApplyQuiet(); return; }
                bool changed = StateChanged(_state, updated);
                _state = updated;
                if (changed) _changed?.Invoke(this);
                ApplyQuiet();
            }
            /// <summary>Makes the authored system silent once its native identity is known: no decorative
            /// traffic at its stations, gates or wormholes, and no security patrols. Idempotent, and
            /// re-resolves across reloads because the declaration anchors on the system identity.</summary>
            private void ApplyQuiet()
            {
                try
                {
                    if (_quiet != null || !Definition.Quiet || _state.SystemId is not { Length: > 0 } systemId) return;
                    var ambient = _service._ambient;
                    if (ambient == null) return;
                    _quiet = ambient.SuppressInSystemContaining(systemId, _localId + "|" + _poiKey + "|quiet", includeSecurityPatrols: true);
                }
                catch (Exception error) { _service._hub.ReportSubscriberFailure("world.quiet-system", error); }
            }
            private void ReleaseQuiet()
            {
                var quiet = _quiet; _quiet = null;
                try { quiet?.Dispose(); } catch { }
            }
            private static bool StateChanged(PocketSystemState a, PocketSystemState b)
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
        _hub.SetUnavailable("world-authoring", health.IsAvailable ? ServiceUnavailableReason.ApiStopped : health.Reason, health.IsAvailable ? "World service stopped." : health.Detail);
    }
}
