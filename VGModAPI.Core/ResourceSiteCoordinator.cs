using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core;

/// <summary>Host-authenticated live authored-site declarations. Registration neither creates native objects nor overwrites saved pois.</summary>
internal sealed class ResourceSiteRegistry : IDisposable
{
    internal sealed class Provider : IDisposable
    {
        private readonly ResourceSiteRegistry _registry;
        internal string Owner { get; }
        internal Provider(ResourceSiteRegistry registry, string owner) { _registry = registry; Owner = owner; }
        public void Dispose() => _registry.Release(this);
    }
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action _checkThread;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), ResourceSiteDeclaration> _definitions = new();
    private readonly Dictionary<(string Owner, string Local), ResourceSiteDeclaration> _previous = new();
    private bool _disposed;
    internal ResourceSiteRegistry(StoryHostAuthenticator authenticate, Action checkThread)
    { _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate)); _checkThread = checkThread ?? throw new ArgumentNullException(nameof(checkThread)); }

    internal Provider? Acquire(object pluginInstance, Assembly caller)
    {
        _checkThread(); if (_disposed) return null;
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, caller); } catch { return null; }
        if (_disposed || plugin == null || !ReferenceEquals(plugin.Assembly, caller)) return null;
        if (_providers.ContainsKey(plugin.PluginId) || _providers.Count >= 32) return null;
        var provider = new Provider(this, plugin.PluginId);
        _providers.Add(plugin.PluginId, provider); return provider;
    }
    internal bool Register(Provider provider, ResourceSiteDeclaration definition, ResourceSiteDeclaration? previous)
    {
        _checkThread();
        if (previous != null && (previous.LocalId != definition.LocalId || previous.Revision >= definition.Revision))
            throw new ArgumentException("Migration must retain local identity and advance revision.", nameof(previous));
        if (!Active(provider) || _definitions.Count >= WorldSerializationAssociation.MaxObjects) return false;
        var key = (provider.Owner, definition.LocalId);
        if (_definitions.ContainsKey(key)) return false;
        _definitions.Add(key, definition);
        if (previous != null) _previous.Add(key, previous);
        return true;
    }
    internal bool TryResolve(Provider provider, string localId, out ResourceSiteDeclaration? definition)
    {
        _checkThread(); definition = null;
        if (provider == null || !Active(provider) || !_definitions.TryGetValue((provider.Owner, localId), out var resolved)) return false;
        definition = resolved; return true;
    }
    internal bool TryResolveMigration(string owner, string localId, out int liveRevision, out int? previousRevision)
    {
        _checkThread(); liveRevision = 0; previousRevision = null;
        if (_disposed || !_providers.ContainsKey(owner) || !_definitions.TryGetValue((owner, localId), out var definition)) return false;
        liveRevision = definition.Revision;
        if (_previous.TryGetValue((owner, localId), out var previous) && previous.Revision < definition.Revision) previousRevision = previous.Revision;
        return true;
    }
    private bool Active(Provider provider) => !_disposed && _providers.TryGetValue(provider.Owner, out var current) && ReferenceEquals(current, provider);
    private void Release(Provider provider)
    {
        _checkThread(); if (!Active(provider)) return;
        _providers.Remove(provider.Owner);
        foreach (var key in _definitions.Keys.Where(key => key.Owner == provider.Owner).ToArray())
        { _definitions.Remove(key); _previous.Remove(key); }
    }
    public void Dispose()
    { _checkThread(); if (_disposed) return; _disposed = true; _providers.Clear(); _definitions.Clear(); _previous.Clear(); }
}

/// <summary>Reflection-driven native seam for authored sites; tests supply fakes. Core never references native Unity types.</summary>
internal interface IResourceSiteNative
{
    /// <summary>Creates the authored site inside an existing system at the given position. Returns the native POI identity, or null as a typed refusal (missing system, unknown wreck class or faction, or a foreign membership delta).</summary>
    string? CreateSite(Guid session, string systemId, float x, float y, ResourceSiteDeclaration declaration);
    /// <summary>Re-resolves the owned site structurally (host system membership + kind match); null until native construction surfaces it.</summary>
    string? ResolveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind);
    /// <summary>Number of native POIs currently bearing the given identity (ambiguity detection).</summary>
    int AmbiguousCount(Guid session, string poiId);
    /// <summary>Removes the owned site POI from its host system (plain: no transient player-safety
    /// refusals). Only succeeds when the post-removal membership delta is exactly this one POI (rollback otherwise).</summary>
    ResourceSiteRemoveOutcome RemoveSite(Guid session, string systemId, string poiId, ResourceSiteKind kind);
    /// <summary>Pure readiness for removing the owned site (no mutation): Ready/PlayerInside/BoardingActive/InteriorPersisted/NotPresent/Unavailable.</summary>
    RemovalStatus Readiness(Guid session, string systemId, string poiId, ResourceSiteKind kind);
    void BeginPass(Guid session);
    void EndPass();
}

/// <summary>Typed outcome of a native authored-site removal attempt.</summary>
internal enum ResourceSiteRemoveOutcome
{
    /// <summary>The site POI was removed from its host system; it is gone from the live map.</summary>
    Removed,
    /// <summary>The player's current location or a waypoint is at the site; nothing was removed.</summary>
    PlayerInside,
    /// <summary>The salvage site's derelict station has a live boarding operation; nothing was removed.</summary>
    BoardingActive,
    /// <summary>The salvage site's derelict station has a persisted interior simulation; nothing was removed.</summary>
    InteriorPersisted,
    /// <summary>The owned site POI is not currently present natively; nothing was removed.</summary>
    Missing,
    /// <summary>The native removal could not be performed or verified; the map may be unchanged.</summary>
    Failed
}

/// <summary>
/// Keyed-owned reconciliation for authored sites. One poi row per (owner, local, key); the API
/// owns the native POI identity, re-declaring the same key reconciles rather than duplicating, and a
/// once-per-session settled event reports actual outcomes. Native site content is persisted by the
/// game's own persistable pipeline; the coordinator re-resolves identity only.
/// </summary>
internal sealed class ResourceSiteCoordinator : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly ResourceSiteRegistry _definitions;
    private readonly IResourceSiteNative _native;
    private readonly Func<Guid, bool> _persistenceReady;
    private readonly Action<Exception> _report;
    private readonly IDisposable _subscription;
    private readonly Dictionary<(string Owner, string Local, string Key), ResourceSitePoi> _committed = new();
    private readonly HashSet<(string Owner, string Local, string Key)> _failed = new();
    private Action<Guid>? _settled;
    private Func<string, Guid?>? _resolveAttachedDungeon;
    private Func<Guid, bool>? _dropDungeon;
    private Guid _session;
    private bool _settledOnce;
    private bool _disposed;

    internal ResourceSiteCoordinator(LifecycleHub hub, ResourceSiteRegistry definitions, IResourceSiteNative native,
        Func<Guid, bool> persistenceReady, Action<Exception> report)
    {
        _hub = hub; _definitions = definitions; _native = native; _persistenceReady = persistenceReady; _report = report;
        _subscription = hub.Subscribe("vgmodapi.authored-sites", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id) Reset();
            else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session) Reset();
        });
    }
    internal void AttachSettled(Action<Guid> settled) { _hub.CheckThread(); _settled = settled; }
    /// <summary>
    /// Bridges the dungeon-content layer so a site's attached authored dungeon poi can be
    /// dropped when the site is removed. The resolver runs before native removal (while the native
    /// location is still resolvable) and the drop runs only after a verified removal, so a failed
    /// removal never strands or loses dungeon state.
    /// </summary>
    internal void AttachDungeonPrune(Func<string, Guid?> resolveAttached, Func<Guid, bool> drop)
    {
        _hub.CheckThread();
        _resolveAttachedDungeon = resolveAttached ?? throw new ArgumentNullException(nameof(resolveAttached));
        _dropDungeon = drop ?? throw new ArgumentNullException(nameof(drop));
    }
    private void Reset()
    { _committed.Clear(); _failed.Clear(); _session = _hub.CurrentSession?.Id ?? Guid.Empty; _settledOnce = false; }
    private Guid Session() => _hub.CurrentSession?.Id ?? Guid.Empty;

    internal void RestoreRows(Guid session, ResourceSitePoi[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-site restore.");
        _committed.Clear();
        if (rows != null) foreach (var row in rows) _committed[(row.Owner, row.LocalId, row.PoiKey)] = row;
        _settledOnce = false;
    }
    internal ResourceSitePoi[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }

    internal IReadOnlyList<ResourceSitePoi> Pois(string owner)
    {
        _hub.CheckThread();
        return _disposed ? Array.Empty<ResourceSitePoi>() : _committed.Values.Where(o => o.Owner == owner).ToArray();
    }
    internal ResourceSitePoi? TryGetPoi(string owner, string localId, string poiKey)
    {
        _hub.CheckThread();
        return _disposed ? null : _committed.TryGetValue((owner, localId, poiKey), out var row) ? row : null;
    }
    /// <summary>Committed or failed: the key is claimed by this kind for the session either way.</summary>
    internal bool ContainsPoi(string owner, string localId, string poiKey)
    {
        _hub.CheckThread();
        return !_disposed && (_committed.ContainsKey((owner, localId, poiKey)) || _failed.Contains((owner, localId, poiKey)));
    }

    /// <summary>
    /// Drops every retained site row inside a removed pocket system (any owner — the native POIs are
    /// removed with the system either way) so save data records them as intentionally absent rather
    /// than reporting them as reconstruction failures. Returns the dropped poi identities.
    /// </summary>
    internal (string Owner, string LocalId, string PoiKey)[] DropPoisInSystem(string systemId)
    {
        _hub.CheckThread();
        if (_disposed || string.IsNullOrEmpty(systemId)) return Array.Empty<(string, string, string)>();
        var dropped = _committed.Where(pair => pair.Value.SystemId == systemId).Select(pair => pair.Key).ToArray();
        foreach (var key in dropped) { _committed.Remove(key); _failed.Remove(key); }
        return dropped;
    }

    /// <summary>
    /// Read-only capture of the authored dungeon pois attached to committed sites inside a system,
    /// resolved BEFORE that system is removed natively. A site's attached dungeon is found through its
    /// live native POI, so once the host system is gone nothing can be matched any more. Null means a
    /// read faulted: the caller must refuse the removal rather than leak a row that could never bind
    /// again. An empty array means there is nothing attached to drop.
    /// </summary>
    internal Guid[]? CaptureDungeonPrune(string systemId)
    {
        _hub.CheckThread();
        if (_disposed || string.IsNullOrEmpty(systemId) || _resolveAttachedDungeon == null) return Array.Empty<Guid>();
        var captured = new List<Guid>();
        foreach (var row in _committed.Values)
        {
            if (row.SystemId != systemId || row.PoiId is not { Length: > 0 } attachedPoi) continue;
            try { if (_resolveAttachedDungeon(attachedPoi) is { } attached) captured.Add(attached); }
            catch (Exception error) { _report(error); return null; }
        }
        return captured.ToArray();
    }

    /// <summary>
    /// Drops the captured authored dungeon pois after a verified system removal, so each row records
    /// its absence instead of surviving as a dead entry. Residual risk matches the direct path: if a
    /// row cannot be dropped at that instant the system is still removed and the fault is reported.
    /// </summary>
    internal void CommitDungeonPrune(Guid[] captured)
    {
        _hub.CheckThread();
        if (_disposed || captured == null || _dropDungeon == null) return;
        foreach (var poi in captured)
        {
            if (!_dropDungeon(poi))
                _report(new InvalidOperationException("An attached authored dungeon poi could not be dropped after pocket removal."));
        }
    }

    internal (WorldContentStatus Status, ResourceSitePoi? Row) Create(ResourceSiteRegistry.Provider provider,
        Guid expectedSession, string localId, string poiKey, string systemId, float x, float y)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldContentStatus.Unavailable, null);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var declaration) || declaration == null)
            return (WorldContentStatus.NotRegistered, null);
        var key = (provider.Owner, localId, poiKey);
        if (_committed.TryGetValue(key, out var owned)) return (WorldContentStatus.Succeeded, owned);
        if (_failed.Contains(key)) return (WorldContentStatus.Rejected, null);
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) return (WorldContentStatus.InvalidDefinition, null);
        if (_committed.Count >= WorldSerializationAssociation.MaxObjects) return (WorldContentStatus.Rejected, null);
        try
        {
            // Allocate native identity only here; never adopt a foreign or ambiguous native identity.
            var poiId = _native.CreateSite(expectedSession, systemId, x, y, declaration);
            if (poiId == null) { _failed.Add(key); return (WorldContentStatus.Rejected, null); }
            var poi = new ResourceSitePoi(provider.Owner, localId, poiKey, declaration.Revision,
                declaration.Kind, systemId, poiId);
            _committed[key] = poi;
            return (WorldContentStatus.Succeeded, poi);
        }
        catch (Exception error) { _report(error); return (WorldContentStatus.Unavailable, null); }
    }

    /// <summary>
    /// Removes the owned poi: removes the native POI from its host system and drops the row so
    /// save data records the poi as intentionally absent rather than reconstructing it as a
    /// failure. Refuses typed: the key is creatable again only after a verified removal. Residual risk:
    /// if an attached authored dungeon row cannot be dropped after a successful site removal (persistence
    /// is not mutable at that instant), the site is still removed and the fault reported; the surviving
    /// row can still surface through <c>IDungeonProvider.GetPois()</c>.
    /// </summary>
    internal (WorldContentStatus Status, string Detail) Remove(ResourceSiteRegistry.Provider provider, Guid session, string local, string key)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldContentStatus.Unavailable, "Authored sites are unavailable.");
        if (provider == null) return (WorldContentStatus.NotRegistered, "");
        var rowKey = (provider.Owner, local, key);
        // A creation that never produced a native POI is still an owned key; removing it frees the key.
        if (_failed.Remove(rowKey)) return (WorldContentStatus.Succeeded, "");
        if (!_committed.TryGetValue(rowKey, out var row)) return (WorldContentStatus.NotRegistered, "");
        // Resolve any attached authored dungeon poi before removal; the native location is no
        // longer discoverable once the POI is gone.
        Guid? attachedDungeon = null;
        if (_resolveAttachedDungeon != null && row.PoiId is { Length: > 0 } attachedPoi)
        {
            try { attachedDungeon = _resolveAttachedDungeon(attachedPoi); }
            catch (Exception error) { _report(error); return (WorldContentStatus.Unavailable, "The site's attached dungeon state could not be read."); }
        }
        try
        {
            var outcome = _native.RemoveSite(session, row.SystemId, row.PoiId, row.Kind);
            switch (outcome)
            {
                case ResourceSiteRemoveOutcome.Removed:
                    _committed.Remove(rowKey);
                    // The native site is gone; dropping the attached row records its absence rather than
                    // leaving a dead poi that could never bind again.
                    if (attachedDungeon.HasValue && _dropDungeon != null && !_dropDungeon(attachedDungeon.Value))
                        _report(new InvalidOperationException("An attached authored dungeon poi could not be dropped after site removal."));
                    return (WorldContentStatus.Succeeded, "");
                case ResourceSiteRemoveOutcome.Missing:
                    return (WorldContentStatus.Rejected, "The site is not currently present natively; wait for reconstruction or check its state.");
                default:
                    return (WorldContentStatus.Rejected, "The native removal could not be performed or verified.");
            }
        }
        catch (Exception error) { _report(error); return (WorldContentStatus.Unavailable, "The native removal faulted."); }
    }

    /// <summary>
    /// Pure readiness for removing the owned site (no mutation): Ready, PlayerInside, BoardingActive,
    /// InteriorPersisted, HeldEnterable, NotPresent or Unavailable. A failed-creation key is Ready
    /// (removing it only frees the key).
    /// </summary>
    internal RemovalStatus CanRemove(ResourceSiteRegistry.Provider provider, Guid session, string local, string key)
    {
        _hub.CheckThread();
        if (_disposed || provider == null) return RemovalStatus.Unavailable;
        var rowKey = (provider.Owner, local, key);
        if (_failed.Contains(rowKey)) return RemovalStatus.Ready;
        if (!_committed.TryGetValue(rowKey, out var row)) return RemovalStatus.NotPresent;
        if (row.PoiId is { Length: > 0 } held
            && _hub.Installations.Aegis.DeclaredTargets().Any(poi => string.Equals(poi, held, StringComparison.Ordinal)))
            return RemovalStatus.HeldEnterable;
        if (_resolveAttachedDungeon != null && row.PoiId is { Length: > 0 } attachedPoi)
        {
            try { _ = _resolveAttachedDungeon(attachedPoi); }
            catch (Exception error) { _report(error); return RemovalStatus.Unavailable; }
        }
        return _native.Readiness(session, row.SystemId, row.PoiId, row.Kind);
    }

    internal ResourceSiteState ReconstructionState(string owner, string localId, string poiKey)
    {
        _hub.CheckThread();
        if (_disposed) return new ResourceSiteState(ReconstructionStatus.Pending);
        if (_failed.Contains((owner, localId, poiKey)))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing);
        return _committed.TryGetValue((owner, localId, poiKey), out var row)
            ? Resolve(row) : new ResourceSiteState(ReconstructionStatus.Pending);
    }

    /// <summary>Reconciled-invariant pass: finalize the once-per-session settled report at the gameplay boundary.</summary>
    internal void Reconcile(Guid expectedSession)
    {
        _hub.CheckThread();
        if (_disposed || expectedSession == Guid.Empty || expectedSession != Session()) return;
        if (_settledOnce || _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized) return;
        _settledOnce = true;
        try { _settled?.Invoke(expectedSession); } catch (Exception error) { _report(error); }
    }

    /// <summary>The site's POI while its owned poi is reconstructed in the loaded game, or null.</summary>
    internal string? ResolveDestination(string owner, string localId, string poiKey)
    {
        _hub.CheckThread();
        if (_disposed) return null;
        var poi = TryGetPoi(owner, localId, poiKey);
        if (poi == null) return null;
        var state = Resolve(poi);
        return state.Status == ReconstructionStatus.Reconstructed ? state.PoiId : null;
    }

    internal ResourceSiteState Resolve(ResourceSitePoi poi)
    {
        _hub.CheckThread();
        if (!_definitions.TryResolveMigration(poi.Owner, poi.LocalId, out var liveRevision, out var previousRevision))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.MissingDefinition);
        if (liveRevision != poi.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == poi.Revision && previousRevision.Value < liveRevision)
                poi.MigrateRevision(liveRevision);
            else return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.PersistenceUnavailable);
        try
        {
            if (_native.AmbiguousCount(Session(), poi.PoiId) > 1)
                return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.AmbiguousIdentity);
            var poiId = _native.ResolveSite(Session(), poi.SystemId, poi.PoiId, poi.Kind);
            return poiId == null
                ? new ResourceSiteState(ReconstructionStatus.Pending)
                : new ResourceSiteState(ReconstructionStatus.Reconstructed, poiId: poiId);
        }
        catch (Exception error)
        { _report(error); return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing); }
    }

    internal void BeginPass(Guid session) => _native.BeginPass(session);
    internal void EndPass() => _native.EndPass();
    /// <summary>Classifies a still-pending poi for the settled report, mirroring the systems coordinator.</summary>
    internal ReconstructionFailureReason PendingReason(Guid session)
        => _persistenceReady(session) ? ReconstructionFailureReason.NativeMissing : ReconstructionFailureReason.PersistenceUnavailable;

    public void Dispose()
    { _hub.CheckThread(); if (_disposed) return; _disposed = true; _subscription.Dispose(); _committed.Clear(); _failed.Clear(); }
}
