using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VGModAPI.Core;

/// <summary>Immutable declarative moored-ship properties.</summary>
internal sealed class MooredShipDeclaration
{
    internal string LocalId { get; }
    internal int Revision { get; }
    internal string Name { get; }
    internal string ShipClassId { get; }
    internal string FactionId { get; }
    internal float OffsetX { get; }
    internal float OffsetY { get; }
    internal bool Protect { get; }
    internal MooredShipDeclaration(MooredShipDefinition definition)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        _ = new ContentDeclaration("vgmodapi.world", definition.LocalId, PersistentContentKind.WorldObject, ContentPersistenceImpact.ApiDependent);
        _ = new ContentDeclaration("vgmodapi.world", definition.FactionId, PersistentContentKind.Faction, ContentPersistenceImpact.ApiDependent);
        if (string.IsNullOrWhiteSpace(definition.Name) || definition.Name.IndexOf('\0') >= 0 || WorldStateCodec.TextByteCount(definition.Name) > 64)
            throw new ArgumentException("A bounded moored-ship display name is required.");
        if (WorldStateCodec.TextByteCount(definition.ShipClassId) > 128) throw new ArgumentException("A bounded ship class is required.");
        LocalId = definition.LocalId; Revision = definition.Revision; Name = definition.Name;
        ShipClassId = definition.ShipClassId; FactionId = definition.FactionId;
        OffsetX = definition.OffsetX; OffsetY = definition.OffsetY; Protect = definition.Protect;
    }
    internal MooredShipDefinition ToDefinition()
        => new(LocalId, Revision, Name, ShipClassId, FactionId, OffsetX, OffsetY, Protect);
}

/// <summary>One persisted moored-ship occurrence row: retained identity plus owned per-game native references.</summary>
internal sealed class MooredShipOccurrence
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string OccurrenceKey { get; }
    internal int Revision { get; private set; }
    internal string StationPoiId { get; }
    internal string UnitId { get; }
    internal MooredShipOccurrence(string owner, string localId, string occurrenceKey, int revision, string stationPoiId, string unitId)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(occurrenceKey) || WorldStateCodec.TextByteCount(occurrenceKey) > 256) throw new ArgumentException("A bounded occurrence key is required.", nameof(occurrenceKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(stationPoiId) || WorldStateCodec.TextByteCount(stationPoiId) > 128) throw new ArgumentException("A bounded native POI identity is required.", nameof(stationPoiId));
        if (string.IsNullOrWhiteSpace(unitId) || WorldStateCodec.TextByteCount(unitId) > 128) throw new ArgumentException("A bounded native unit identity is required.", nameof(unitId));
        Owner = owner; LocalId = localId; OccurrenceKey = occurrenceKey; Revision = revision;
        StationPoiId = stationPoiId; UnitId = unitId;
    }
    internal void MigrateRevision(int revision)
    {
        if (revision < 1 || revision <= Revision) throw new ArgumentOutOfRangeException(nameof(revision));
        Revision = revision;
    }
}

/// <summary>Host-authenticated live moored-ship declarations.</summary>
internal sealed class MooredShipRegistry : IDisposable
{
    internal sealed class Provider : IDisposable
    {
        private readonly MooredShipRegistry _registry;
        internal string Owner { get; }
        internal Provider(MooredShipRegistry registry, string owner) { _registry = registry; Owner = owner; }
        public void Dispose() => _registry.Release(this);
    }
    private readonly StoryHostAuthenticator _authenticate;
    private readonly Action _checkThread;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), MooredShipDeclaration> _definitions = new();
    private readonly Dictionary<(string Owner, string Local), MooredShipDeclaration> _previous = new();
    private bool _disposed;
    internal MooredShipRegistry(StoryHostAuthenticator authenticate, Action checkThread)
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
    internal bool Register(Provider provider, MooredShipDeclaration definition, MooredShipDeclaration? previous)
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
    internal bool TryResolve(Provider provider, string localId, out MooredShipDeclaration? definition)
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
    internal MooredShipDeclaration? Resolve(string owner, string localId)
    {
        _checkThread();
        return !_disposed && _definitions.TryGetValue((owner, localId), out var declaration) ? declaration : null;
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

/// <summary>Reflection-driven native seam for moored ships; tests supply fakes.</summary>
internal interface IMooredShipNative
{
    /// <summary>Spawns (or queues) the moored ship datum at the station and returns the persistent unit identity, or null as a typed refusal.</summary>
    string? CreateShip(Guid session, string stationPoiId, MooredShipDeclaration declaration);
    /// <summary>Whether the station's persisted unit data currently contains the owned identity.</summary>
    bool ResolveShip(Guid session, string stationPoiId, string unitId);
    /// <summary>Idempotent per-frame mooring maintenance of the live instance, when present.</summary>
    void Maintain(Guid session, string stationPoiId, string unitId, MooredShipDeclaration declaration);
}

/// <summary>Keyed-owned reconciliation for moored authored ships; the API owns the persistent unit identity.</summary>
internal sealed class MooredShipCoordinator : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly MooredShipRegistry _definitions;
    private readonly IMooredShipNative _native;
    private readonly Func<Guid, bool> _persistenceReady;
    private readonly Action<string, string?> _protect;
    private readonly Action<Exception> _report;
    private readonly IDisposable _subscription;
    private readonly Dictionary<(string Owner, string Local, string Key), MooredShipOccurrence> _committed = new();
    private readonly HashSet<(string Owner, string Local, string Key)> _failed = new();
    private Action<Guid>? _settled;
    private Guid _session;
    private bool _settledOnce;
    private bool _disposed;

    internal MooredShipCoordinator(LifecycleHub hub, MooredShipRegistry definitions, IMooredShipNative native,
        Func<Guid, bool> persistenceReady, Action<string, string?> protect, Action<Exception> report)
    {
        _hub = hub; _definitions = definitions; _native = native; _persistenceReady = persistenceReady;
        _protect = protect; _report = report;
        _subscription = hub.Subscribe("vgmodapi.authored-ships", e =>
        {
            if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id) Reset();
            else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session) Reset();
        });
    }
    internal void AttachSettled(Action<Guid> settled) { _hub.CheckThread(); _settled = settled; }
    private void Reset()
    { _committed.Clear(); _failed.Clear(); _session = _hub.CurrentSession?.Id ?? Guid.Empty; _settledOnce = false; }
    private Guid Session() => _hub.CurrentSession?.Id ?? Guid.Empty;

    internal void RestoreRows(Guid session, MooredShipOccurrence[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-ship restore.");
        _committed.Clear();
        if (rows != null) foreach (var row in rows) _committed[(row.Owner, row.LocalId, row.OccurrenceKey)] = row;
        _settledOnce = false;
    }
    internal MooredShipOccurrence[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }
    internal IReadOnlyList<MooredShipOccurrence> Occurrences(string owner)
    { _hub.CheckThread(); return _disposed ? Array.Empty<MooredShipOccurrence>() : _committed.Values.Where(o => o.Owner == owner).ToArray(); }
    internal MooredShipOccurrence? TryGetOccurrence(string owner, string localId, string occurrenceKey)
    { _hub.CheckThread(); return _disposed ? null : _committed.TryGetValue((owner, localId, occurrenceKey), out var row) ? row : null; }
    internal bool ContainsOccurrence(string owner, string localId, string occurrenceKey)
    { _hub.CheckThread(); return TryGetOccurrence(owner, localId, occurrenceKey) != null || _failed.Contains((owner, localId, occurrenceKey)); }

    internal (WorldStatus Status, MooredShipOccurrence? Row) Create(MooredShipRegistry.Provider provider,
        Guid expectedSession, string localId, string occurrenceKey, string stationPoiId)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldStatus.Unavailable, null);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var declaration) || declaration == null)
            return (WorldStatus.NotRegistered, null);
        var key = (provider.Owner, localId, occurrenceKey);
        if (_committed.TryGetValue(key, out var owned)) return (WorldStatus.Succeeded, owned);
        if (_failed.Contains(key)) return (WorldStatus.Rejected, null);
        if (string.IsNullOrWhiteSpace(stationPoiId) || WorldStateCodec.TextByteCount(stationPoiId) > 128) return (WorldStatus.InvalidDefinition, null);
        if (_committed.Count >= WorldSerializationAssociation.MaxObjects) return (WorldStatus.Rejected, null);
        try
        {
            var unitId = _native.CreateShip(expectedSession, stationPoiId, declaration);
            if (unitId == null) { _failed.Add(key); return (WorldStatus.Rejected, null); }
            var occurrence = new MooredShipOccurrence(provider.Owner, localId, occurrenceKey, declaration.Revision, stationPoiId, unitId);
            _committed[key] = occurrence;
            if (declaration.Protect) TryProtect(occurrence);
            return (WorldStatus.Succeeded, occurrence);
        }
        catch (Exception error) { _report(error); return (WorldStatus.Unavailable, null); }
    }

    internal MooredShipState ReconstructionState(string owner, string localId, string occurrenceKey)
    {
        _hub.CheckThread();
        if (_disposed) return new MooredShipState(ReconstructionStatus.Pending);
        if (_failed.Contains((owner, localId, occurrenceKey)))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing);
        return _committed.TryGetValue((owner, localId, occurrenceKey), out var row)
            ? Resolve(row) : new MooredShipState(ReconstructionStatus.Pending);
    }

    /// <summary>Reconciled-invariant pass: maintain live moorings/protection and finalize the once-per-session settled report.</summary>
    internal void Reconcile(Guid expectedSession)
    {
        _hub.CheckThread();
        if (_disposed || expectedSession == Guid.Empty || expectedSession != Session()) return;
        foreach (var occurrence in _committed.Values.ToArray())
        {
            var declaration = _definitions.Resolve(occurrence.Owner, occurrence.LocalId);
            if (declaration == null) continue;
            try
            {
                _native.Maintain(expectedSession, occurrence.StationPoiId, occurrence.UnitId, declaration);
                if (declaration.Protect) TryProtect(occurrence);
            }
            catch (Exception error) { _report(error); }
        }
        if (_settledOnce || _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized) return;
        _settledOnce = true;
        try { _settled?.Invoke(expectedSession); } catch (Exception error) { _report(error); }
    }
    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    private void TryProtect(MooredShipOccurrence occurrence)
    {
        // Keyed API-internal protection: one declaration per occurrence, replaced not accumulated.
        if (!_protected.Add(occurrence.Owner + "\n" + occurrence.LocalId + "\n" + occurrence.OccurrenceKey + "\n" + occurrence.UnitId)) return;
        try { _protect(occurrence.UnitId, "vgmodapi.authored-ship." + occurrence.Owner + "." + occurrence.LocalId + "." + occurrence.OccurrenceKey); }
        catch (Exception error) { _report(error); }
    }

    internal MooredShipState Resolve(MooredShipOccurrence occurrence)
    {
        _hub.CheckThread();
        if (!_definitions.TryResolveMigration(occurrence.Owner, occurrence.LocalId, out var liveRevision, out var previousRevision))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.MissingDefinition);
        if (liveRevision != occurrence.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == occurrence.Revision && previousRevision.Value < liveRevision)
                occurrence.MigrateRevision(liveRevision);
            else return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.PersistenceUnavailable);
        try
        {
            return _native.ResolveShip(Session(), occurrence.StationPoiId, occurrence.UnitId)
                ? new MooredShipState(ReconstructionStatus.Reconstructed, unitId: occurrence.UnitId)
                : new MooredShipState(ReconstructionStatus.Pending);
        }
        catch (Exception error)
        { _report(error); return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing); }
    }
    internal ReconstructionFailureReason PendingReason(Guid session)
        => _persistenceReady(session) ? ReconstructionFailureReason.NativeMissing : ReconstructionFailureReason.PersistenceUnavailable;

    public void Dispose()
    { _hub.CheckThread(); if (_disposed) return; _disposed = true; _subscription.Dispose(); _committed.Clear(); _failed.Clear(); }
}
