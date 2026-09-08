using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>API-owned save participant. Providers do not register serialization callbacks for dungeon content.</summary>
internal sealed class DungeonStateStore : IDisposable
{
    private const string Owner = "vgmodapi.dungeons";
    private readonly LifecycleHub _hub;
    private readonly IPersistenceRegistration? _registration;
    private readonly IDisposable _lifetime;
    private Dictionary<Guid, DungeonOccurrence> _entries = new();
    private Guid? _restoredSession;
    private bool _disposed;
    private int _serializationDepth;
    internal void BeginSerialization() { _hub.CheckThread(); _serializationDepth++; }
    internal void EndSerialization() { _hub.CheckThread(); if (_serializationDepth > 0) _serializationDepth--; }
    internal DungeonStateStore(LifecycleHub hub, IPersistenceApi? persistence)
    {
        _hub = hub;
        _lifetime = hub.Subscribe(Owner, message =>
        {
            if (message.Kind is LifecycleEventKind.SessionStarting or LifecycleEventKind.SessionInvalidated or LifecycleEventKind.SessionStartFailed)
            { _entries.Clear(); _restoredSession = null; }
        });
        _registration = persistence?.Register(new PersistenceProvider(Owner, 1, Capture, Restore, DungeonStateCodec.Validate));
    }
    internal bool StateReady
    {
        get
        {
            _hub.CheckThread();
            return !_disposed && _restoredSession.HasValue && _hub.CurrentSession?.Id == _restoredSession &&
                _registration is IPersistenceReadiness readiness && readiness.StateReady;
        }
    }
    internal bool MutationAllowed => StateReady && _serializationDepth == 0 && _registration!.MutationAllowed && !_hub.IsDispatchingCallbacks;
    internal IReadOnlyList<DungeonOccurrence> Entries
    { get { _hub.CheckThread(); return StateReady ? Array.AsReadOnly(_entries.Values.ToArray()) : Array.Empty<DungeonOccurrence>(); } }
    internal DungeonOccurrence? Get(Guid id)
    { _hub.CheckThread(); return StateReady && _entries.TryGetValue(id, out var entry) ? entry : null; }
    internal bool Add(DungeonOccurrence entry)
    {
        _hub.CheckThread(); if (!MutationAllowed) return false;
        if (_entries.ContainsKey(entry.Id)) throw new InvalidOperationException("Duplicate dungeon occurrence identity.");
        // Refuse before changing the ledger if the complete retained state cannot be saved.
        DungeonStateCodec.Encode(_entries.Values.Concat(new[] { entry }));
        _entries.Add(entry.Id, entry); return true;
    }
    internal bool Choose(Guid id, string provider, string eventId, string choiceId)
    {
        _hub.CheckThread(); if (!MutationAllowed || !_entries.TryGetValue(id, out var entry) || entry.DefinitionId.ProviderId != provider) return false;
        var definition = entry.Definition.Events.SingleOrDefault(e => e.Id == eventId);
        if (definition == null || !definition.Choices.Any(c => c.Id == choiceId) || entry.Choices.ContainsKey(eventId)) return false;
        var choices = new Dictionary<string, string>(entry.Choices, StringComparer.Ordinal) { [eventId] = choiceId };
        var replacement = new DungeonOccurrence(id, entry.DefinitionId, entry.Definition, choices);
        DungeonStateCodec.Encode(_entries.Values.Where(e => e.Id != id).Concat(new[] { replacement }));
        _entries[id] = replacement; return true;
    }
    private byte[] Capture()
    {
        _hub.CheckThread();
        if (_disposed || !_restoredSession.HasValue || _restoredSession != _hub.CurrentSession?.Id) throw new InvalidOperationException("Dungeon state has not been restored for this session.");
        return DungeonStateCodec.Encode(_entries.Values);
    }
    private void Restore(SessionSnapshot session, byte[]? payload)
    {
        _hub.CheckThread();
        if (_disposed || _hub.CurrentSession?.Id != session.Id) throw new InvalidOperationException("Stale dungeon restore.");
        var restored = payload == null ? Array.Empty<DungeonOccurrence>() : DungeonStateCodec.Decode(payload);
        var replacement = restored.ToDictionary(e => e.Id);
        _entries = replacement; _restoredSession = session.Id;
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return; _disposed = true;
        _registration?.Dispose(); _lifetime.Dispose(); _entries.Clear(); _restoredSession = null;
    }
}
