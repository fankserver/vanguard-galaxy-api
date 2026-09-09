using System;
using System.IO;
using System.Linq;

namespace VGModAPI.Core.Integration;

/// <summary>Registers both automatic world owners; restore must match the generation inspected before native construction.</summary>
internal sealed class WorldPersistenceBindings : IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly WorldLoadHookHost _loads;
    private readonly WorldCreationCoordinator _creation;
    private readonly ISaveDataRegistration _state, _definitions;
    private bool _disposed;
    internal WorldPersistenceBindings(ISaveDataService persistence, LifecycleHub hub, WorldLoadHookHost loads,
        WorldSnapshotHookHost snapshots, WorldCreationCoordinator creation)
    {
        _hub = hub; _loads = loads; _creation = creation; _hub.CheckThread();
        _state = persistence.Register(new PersistenceProvider(WorldStateCodec.Owner, WorldStateCodec.SchemaVersion,
            () => snapshots.CaptureOwner(WorldStateCodec.Owner), (session, payload) => Restore(WorldStateCodec.Owner, session, payload),
            payload => Validate(payload, false))).Registration ?? throw new InvalidOperationException("World state registration refused.");
        try
        {
            _definitions = persistence.Register(new PersistenceProvider(WorldDefinitionCodec.Owner, WorldDefinitionCodec.SchemaVersion,
                () => snapshots.CaptureOwner(WorldDefinitionCodec.Owner), (session, payload) => Restore(WorldDefinitionCodec.Owner, session, payload),
                payload => Validate(payload, true))).Registration ?? throw new InvalidOperationException("World definitions registration refused.");
        }
        catch { _state.Dispose(); throw; }
    }
    private static bool Validate(byte[] payload, bool definitions)
    {
        try { if (definitions) WorldDefinitionCodec.Decode(payload); else WorldStateCodec.Decode(payload); return true; }
        catch (InvalidDataException) { return false; }
    }
    private void Restore(string owner, SessionSnapshot session, byte[]? payload)
    {
        _hub.CheckThread();
        if (_disposed || _hub.CurrentSession?.Id != session.Id) throw new InvalidDataException("Stale world persistence restore.");
        byte[]? expected = null;
        if (session.Origin != SessionOrigin.NewGame)
        {
            var prepared = _loads.PreparedFor(session.Id) ?? throw new InvalidDataException("World persistence lacks verified early load metadata.");
            expected = prepared.Generation?.PayloadFor(owner);
        }
        if (_disposed || _hub.CurrentSession?.Id != session.Id ||
            (expected == null ? payload != null : payload == null || !expected.SequenceEqual(payload)))
            throw new InvalidDataException("World persistence differs from the admitted native generation.");
    }
    internal bool StateReady(Guid session)
    {
        _hub.CheckThread();
        return !_disposed && _hub.CurrentSession?.Id == session && _creation.HasRestoredInventory(session) &&
            Ready(_state, session) && Ready(_definitions, session);
    }
    internal bool CanMutate(Guid session)
    {
        _hub.CheckThread();
        return !_disposed && _hub.CurrentSession?.Id == session && _creation.HasRestoredInventory(session) &&
            Ready(_state, session) && Ready(_definitions, session) && _state.CanMutate && _definitions.CanMutate;
    }
    private static bool Ready(ISaveDataRegistration registration, Guid session)
        => registration.State.Kind == SaveDataStateKind.Ready && registration.State.SessionId == session;
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true;
        try { _state.Dispose(); } finally { _definitions.Dispose(); }
    }
}
