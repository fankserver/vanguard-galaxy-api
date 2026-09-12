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
        _ = new PersistentDeclaration("vgmodapi.world", definition.LocalId, PersistentKind.WorldObject, PersistenceImpact.ApiDependent);
        _ = new PersistentDeclaration("vgmodapi.world", definition.FactionId, PersistentKind.Faction, PersistenceImpact.ApiDependent);
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

/// <summary>One persisted moored-ship unit row: retained identity plus owned per-game native references.</summary>
internal sealed class MooredShipUnit
{
    internal string Owner { get; }
    internal string LocalId { get; }
    internal string UnitKey { get; }
    internal int Revision { get; private set; }
    internal string StationPoiId { get; }
    internal string UnitId { get; }
    internal MooredShipUnit(string owner, string localId, string unitKey, int revision, string stationPoiId, string unitId)
    {
        if (string.IsNullOrWhiteSpace(owner) || WorldStateCodec.TextByteCount(owner) > 128) throw new ArgumentException("A bounded owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(localId) || WorldStateCodec.TextByteCount(localId) > 128) throw new ArgumentException("A bounded local identity is required.", nameof(localId));
        if (string.IsNullOrWhiteSpace(unitKey) || WorldStateCodec.TextByteCount(unitKey) > 256) throw new ArgumentException("A bounded unit key is required.", nameof(unitKey));
        if (revision < 1) throw new ArgumentOutOfRangeException(nameof(revision));
        if (string.IsNullOrWhiteSpace(stationPoiId) || WorldStateCodec.TextByteCount(stationPoiId) > 128) throw new ArgumentException("A bounded native POI identity is required.", nameof(stationPoiId));
        if (string.IsNullOrWhiteSpace(unitId) || WorldStateCodec.TextByteCount(unitId) > 128) throw new ArgumentException("A bounded native unit identity is required.", nameof(unitId));
        Owner = owner; LocalId = localId; UnitKey = unitKey; Revision = revision;
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
    private readonly Dictionary<(string Owner, string Local, string Key), MooredShipUnit> _committed = new();
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

    internal void RestoreRows(Guid session, MooredShipUnit[] rows)
    {
        _hub.CheckThread();
        if (_disposed || session == Guid.Empty || session != Session()) throw new InvalidDataException("Stale authored-ship restore.");
        _committed.Clear();
        if (rows != null) foreach (var row in rows) _committed[(row.Owner, row.LocalId, row.UnitKey)] = row;
        _settledOnce = false;
    }
    internal MooredShipUnit[] CaptureRows() { _hub.CheckThread(); return _committed.Values.ToArray(); }
    internal IReadOnlyList<MooredShipUnit> Units(string owner)
    { _hub.CheckThread(); return _disposed ? Array.Empty<MooredShipUnit>() : _committed.Values.Where(o => o.Owner == owner).ToArray(); }
    internal MooredShipUnit? TryGetUnit(string owner, string localId, string unitKey)
    { _hub.CheckThread(); return _disposed ? null : _committed.TryGetValue((owner, localId, unitKey), out var row) ? row : null; }
    internal bool ContainsUnit(string owner, string localId, string unitKey)
    { _hub.CheckThread(); return TryGetUnit(owner, localId, unitKey) != null || _failed.Contains((owner, localId, unitKey)); }

    internal (WorldStatus Status, MooredShipUnit? Row) Create(MooredShipRegistry.Provider provider,
        Guid expectedSession, string localId, string unitKey, string stationPoiId)
    {
        _hub.CheckThread();
        if (_disposed) return (WorldStatus.Unavailable, null);
        if (provider == null || !_definitions.TryResolve(provider, localId, out var declaration) || declaration == null)
            return (WorldStatus.NotRegistered, null);
        var key = (provider.Owner, localId, unitKey);
        if (_committed.TryGetValue(key, out var owned)) return (WorldStatus.Succeeded, owned);
        if (_failed.Contains(key)) return (WorldStatus.Rejected, null);
        if (string.IsNullOrWhiteSpace(stationPoiId) || WorldStateCodec.TextByteCount(stationPoiId) > 128) return (WorldStatus.InvalidDefinition, null);
        if (_committed.Count >= WorldSerializationAssociation.MaxObjects) return (WorldStatus.Rejected, null);
        try
        {
            var unitId = _native.CreateShip(expectedSession, stationPoiId, declaration);
            if (unitId == null) { _failed.Add(key); return (WorldStatus.Rejected, null); }
            var unit = new MooredShipUnit(provider.Owner, localId, unitKey, declaration.Revision, stationPoiId, unitId);
            _committed[key] = unit;
            if (declaration.Protect) TryProtect(unit);
            return (WorldStatus.Succeeded, unit);
        }
        catch (Exception error) { _report(error); return (WorldStatus.Unavailable, null); }
    }

    internal MooredShipState ReconstructionState(string owner, string localId, string unitKey)
    {
        _hub.CheckThread();
        if (_disposed) return new MooredShipState(ReconstructionStatus.Pending);
        if (_failed.Contains((owner, localId, unitKey)))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.NativeMissing);
        return _committed.TryGetValue((owner, localId, unitKey), out var row)
            ? Resolve(row) : new MooredShipState(ReconstructionStatus.Pending);
    }

    /// <summary>Reconciled-invariant pass: maintain live moorings/protection and finalize the once-per-session settled report.</summary>
    internal void Reconcile(Guid expectedSession)
    {
        _hub.CheckThread();
        if (_disposed || expectedSession == Guid.Empty || expectedSession != Session()) return;
        foreach (var unit in _committed.Values.ToArray())
        {
            var declaration = _definitions.Resolve(unit.Owner, unit.LocalId);
            if (declaration == null) continue;
            try
            {
                _native.Maintain(expectedSession, unit.StationPoiId, unit.UnitId, declaration);
                if (declaration.Protect) TryProtect(unit);
            }
            catch (Exception error) { _report(error); }
        }
        if (_settledOnce || _hub.CurrentSession?.Phase != SessionPhase.GameplayInitialized) return;
        _settledOnce = true;
        try { _settled?.Invoke(expectedSession); } catch (Exception error) { _report(error); }
    }
    private readonly HashSet<string> _protected = new(StringComparer.Ordinal);
    private void TryProtect(MooredShipUnit unit)
    {
        // Keyed API-internal protection: one declaration per unit, replaced not accumulated.
        if (!_protected.Add(unit.Owner + "\n" + unit.LocalId + "\n" + unit.UnitKey + "\n" + unit.UnitId)) return;
        try { _protect(unit.UnitId, "vgmodapi.authored-ship." + unit.Owner + "." + unit.LocalId + "." + unit.UnitKey); }
        catch (Exception error) { _report(error); }
    }

    internal MooredShipState Resolve(MooredShipUnit unit)
    {
        _hub.CheckThread();
        if (!_definitions.TryResolveMigration(unit.Owner, unit.LocalId, out var liveRevision, out var previousRevision))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.MissingDefinition);
        if (liveRevision != unit.Revision)
        {
            if (previousRevision.HasValue && previousRevision.Value == unit.Revision && previousRevision.Value < liveRevision)
                unit.MigrateRevision(liveRevision);
            else return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.RevisionMismatch);
        }
        if (!_persistenceReady(Session()))
            return new MooredShipState(ReconstructionStatus.Failed, ReconstructionFailureReason.PersistenceUnavailable);
        try
        {
            return _native.ResolveShip(Session(), unit.StationPoiId, unit.UnitId)
                ? new MooredShipState(ReconstructionStatus.Reconstructed, unitId: unit.UnitId)
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
