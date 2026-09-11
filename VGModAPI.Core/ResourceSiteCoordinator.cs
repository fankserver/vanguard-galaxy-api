using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core;

/// <summary>Host-authenticated live authored-site declarations. Registration neither creates native objects nor overwrites saved instances.</summary>
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
    void BeginPass(Guid session);
    void EndPass();
}

/// <summary>
/// Keyed-owned reconciliation for authored sites. One occurrence row per (owner, local, key); the API
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
    private readonly Dictionary<(string Owner, string Local, string Key), ResourceSiteOccurrence> _committed = new();
    private readonly HashSet<(string Owner, string Local, string Key)> _failed = new();
    private Action<Guid>? _settled;
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
    private void Reset()
    { _committed.Clear(); _failed.Clear(); _session = _hub.CurrentSession?.Id ?? Guid.Empty; _settledOnce = false; }
    private Guid Session() => _hub.CurrentSession?.Id ?? Guid.Empty;

    internal void RestoreRows(Guid session, ResourceSiteOccurrence[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-site restore.");
        _committed.Clear();
        if (rows != null) foreach (var row in rows) _committed[(row.Owner, row.LocalId, row.OccurrenceKey)] = row;
        _settledOnce = false;
    }
    internal ResourceSiteOccurrence[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }

    internal IReadOnlyList<ResourceSiteOccurrence> Occurrences(string owner)
    {
        _hub.CheckThread();
        return _disposed ? Array.Empty<ResourceSiteOccurrence>() : _committed.Values.Where(o => o.Owner == owner).ToArray();
    }
    internal ResourceSiteOccurrence? TryGetOccurrence(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        return _disposed ? null : _committed.TryGetValue((owner, localId, occurrenceKey), out var row) ? row : null;
    }
    /// <summary>Committed or failed: the key is claimed by this kind for the session either way.</summary>
    internal bool ContainsOccurrence(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        return !_disposed && (_committed.ContainsKey((owner, localId, occurrenceKey)) || _failed.Contains((owner, localId, occurrenceKey)));
    }

    /// <summary>
    /// Drops every retained site row inside a dissolved pocket system (any owner — the native POIs are
    /// removed with the system either way) so save data records them as intentionally absent rather
    /// than reporting them as reconstruction failures. Returns the dropped occurrence identities.
    /// </summary>
    internal (string Owner, string LocalId, string OccurrenceKey)[] DropOccurrencesInSystem(string systemId)
    {
        _hub.CheckThread();
        if (_disposed || string.IsNullOrEmpty(systemId)) return Array.Empty<(string, string, string)>();
        var dropped = _committed.Where(pair => pair.Value.SystemId == systemId).Select(pair => pair.Key).ToArray();
        foreach (var key in dropped) { _committed.Remove(key); _failed.Remove(key); }
        return dropped;
    }

    internal (WorldStatus Status, ResourceSiteOccurrence? Row) Create(ResourceSiteRegistry.Provider provider,
        Guid expectedSession, string localId, string occurrenceKey, string systemId, float x, float y)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldStatus.Unavailable, null);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var declaration) || declaration == null)
            return (WorldStatus.NotRegistered, null);
        var key = (provider.Owner, localId, occurrenceKey);
        if (_committed.TryGetValue(key, out var owned)) return (WorldStatus.Succeeded, owned);
        if (_failed.Contains(key)) return (WorldStatus.Rejected, null);
        if (string.IsNullOrWhiteSpace(systemId) || WorldStateCodec.TextByteCount(systemId) > 128) return (WorldStatus.InvalidDefinition, null);
        if (_committed.Count >= WorldSerializationAssociation.MaxObjects) return (WorldStatus.Rejected, null);
        try
        {
            // Allocate native identity only here; never adopt a foreign or ambiguous native identity.
            var poiId = _native.CreateSite(expectedSession, systemId, x, y, declaration);
            if (poiId == null) { _failed.Add(key); return (WorldStatus.Rejected, null); }
            var occurrence = new ResourceSiteOccurrence(provider.Owner, localId, occurrenceKey, declaration.Revision,
                declaration.Kind, systemId, poiId);
            _committed[key] = occurrence;
            return (WorldStatus.Succeeded, occurrence);
        }
        catch (Exception error) { _report(error); return (WorldStatus.Unavailable, null); }
    }

    internal ResourceSiteState ReconstructionState(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return new ResourceSiteState(ReconstructionStatus.Pending);
        if (_failed.Contains((owner, localId, occurrenceKey)))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing);
        return _committed.TryGetValue((owner, localId, occurrenceKey), out var row)
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

    /// <summary>The site's POI while its owned occurrence is reconstructed in the loaded game, or null.</summary>
    internal string? ResolveDestination(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return null;
        var occurrence = TryGetOccurrence(owner, localId, occurrenceKey);
        if (occurrence == null) return null;
        var state = Resolve(occurrence);
        return state.Status == ReconstructionStatus.Reconstructed ? state.PoiId : null;
    }

    internal ResourceSiteState Resolve(ResourceSiteOccurrence occurrence)
    {
        _hub.CheckThread();
        if (!_definitions.TryResolveMigration(occurrence.Owner, occurrence.LocalId, out var liveRevision, out var previousRevision))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.MissingDefinition);
        if (liveRevision != occurrence.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == occurrence.Revision && previousRevision.Value < liveRevision)
                occurrence.MigrateRevision(liveRevision);
            else return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.PersistenceUnavailable);
        try
        {
            if (_native.AmbiguousCount(Session(), occurrence.PoiId) > 1)
                return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.AmbiguousIdentity);
            var poiId = _native.ResolveSite(Session(), occurrence.SystemId, occurrence.PoiId, occurrence.Kind);
            return poiId == null
                ? new ResourceSiteState(ReconstructionStatus.Pending)
                : new ResourceSiteState(ReconstructionStatus.Reconstructed, poiId: poiId);
        }
        catch (Exception error)
        { _report(error); return new ResourceSiteState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing); }
    }

    internal void BeginPass(Guid session) => _native.BeginPass(session);
    internal void EndPass() => _native.EndPass();
    /// <summary>Classifies a still-pending occurrence for the settled report, mirroring the systems coordinator.</summary>
    internal ReconstructionFailureReason PendingReason(Guid session)
        => _persistenceReady(session) ? ReconstructionFailureReason.NativeMissing : ReconstructionFailureReason.PersistenceUnavailable;

    public void Dispose()
    { _hub.CheckThread(); if (_disposed) return; _disposed = true; _subscription.Dispose(); _committed.Clear(); _failed.Clear(); }
}
