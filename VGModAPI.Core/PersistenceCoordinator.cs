using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

// Internal engine. Runtime discovery/registration and consumer migrations are separate deliveries.
internal sealed class PersistenceCoordinator : IDisposable
{
    private sealed class Owner
    {
        internal readonly OwnerSchemaCodec Codec;
        internal readonly Func<byte[]> Capture;
        internal readonly Action<byte[]?> Restore;
        internal bool Ready;
        internal string Status = "inactive";
        internal Owner(OwnerSchemaCodec codec, Func<byte[]> capture, Action<byte[]?> restore)
        { Codec = codec; Capture = capture; Restore = restore; }
    }
    private sealed class Pending
    {
        internal readonly Guid Session, Campaign;
        internal readonly string Destination;
        internal readonly Dictionary<string, byte[]> Owners;
        internal Pending(Guid session, Guid campaign, string destination, Dictionary<string, byte[]> owners)
        { Session = session; Campaign = campaign; Destination = destination; Owners = owners; }
    }
    private readonly LifecycleHub _hub;
    private readonly GenerationStore _store;
    private readonly Func<string, string> _canonical, _hashFile;
    private readonly IDisposable _subscription;
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Pending> _pending = new();
    private readonly Dictionary<Guid, (string Path, Guid? Session)> _intents = new();
    private Dictionary<string, byte[]> _known = new(StringComparer.Ordinal);
    private Guid? _session;
    private Guid _campaign;
    private string? _loadPath, _loadHash;
    private bool _sessionFault, _writeFault, _disposed, _restored;
    private string? _faultDetail;

    internal PersistenceCoordinator(LifecycleHub hub, GenerationStore store, Func<string, string> canonical, Func<string, string> hashFile)
    {
        hub.CheckThread();
        if (hub.CurrentSession != null) throw new InvalidOperationException("Coordinator must start before a session.");
        _hub = hub; _store = store; _canonical = canonical; _hashFile = hashFile;
        _subscription = hub.Subscribe("vgmodapi.persistence", OnEvent);
    }

    internal void Register(OwnerSchemaCodec codec, Func<byte[]> capture, Action<byte[]?> restore)
    {
        _hub.CheckThread();
        if (_disposed || _hub.CurrentSession != null) throw new InvalidOperationException("Register before a session begins.");
        if (codec == null || capture == null || restore == null) throw new ArgumentNullException();
        if (_owners.ContainsKey(codec.Owner)) throw new InvalidOperationException("Owner is already registered.");
        if (_owners.Count >= GenerationStore.MaxOwners) throw new InvalidOperationException("Owner limit reached.");
        _owners.Add(codec.Owner, new Owner(codec, capture, restore));
    }

    internal bool MutationAllowed(string owner)
    {
        _hub.CheckThread();
        return !_disposed && !_sessionFault && !_writeFault && _pending.Count == 0 && _intents.Count == 0 && !_hub.IsDispatchingCallbacks
            && _session.HasValue && Current(_session.Value) && _hub.CurrentSession!.Phase == SessionPhase.GameplayInitialized
            && _owners.TryGetValue(owner, out var registered) && registered.Ready;
    }

    internal string Status(string owner)
    {
        _hub.CheckThread();
        if (_disposed || !_session.HasValue) return "inactive";
        if (_sessionFault) return "load-blocked";
        if (_writeFault) return _faultDetail ?? "publication-blocked";
        return _owners.TryGetValue(owner, out var registered) ? registered.Status : "unregistered";
    }

    private bool Current(Guid id) => !_disposed && _session == id && _hub.CurrentSession?.Id == id
        && _hub.CurrentSession.Phase != SessionPhase.Failed && _hub.CurrentSession.Phase != SessionPhase.Invalidated;

    private void Reset()
    {
        _session = null; _pending.Clear(); _known.Clear(); _loadPath = null; _loadHash = null;
        _sessionFault = false; _writeFault = false; _restored = false; _faultDetail = null;
        foreach (var owner in _owners.Values) { owner.Ready = false; owner.Status = "inactive"; }
    }

    private void OnEvent(LifecycleEvent e)
    {
        if (_disposed) return;
        if (e.Kind == LifecycleEventKind.SessionStarting)
        {
            if (e.Session == null || e.Session.Id != _hub.CurrentSession?.Id) return;
            Reset(); _session = e.Session.Id; _campaign = Guid.NewGuid();
            try
            {
                if (e.Session.Origin == SessionOrigin.SaveLoad)
                {
                    _loadPath = _canonical(e.Session.SavePath ?? throw new InvalidOperationException("Missing source."));
                    _loadHash = _hashFile(_loadPath);
                }
            }
            catch { _sessionFault = true; }
            return;
        }
        if (e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed)
        { if (e.Session?.Id == _session) Reset(); return; }
        if (e.Kind == LifecycleEventKind.SaveStarted)
        {
            try
            {
                if (e.OperationId == null || e.Destination == null) throw new InvalidOperationException("Invalid save start.");
                if (_intents.ContainsKey(e.OperationId.Value))
                { _pending.Remove(e.OperationId.Value); throw new InvalidOperationException("Duplicate save start."); }
                var path = _canonical(e.Destination);
                _store.MarkIntent(path, e.OperationId.Value);
                _intents.Add(e.OperationId.Value, (path, e.Session?.Id));
                if (_session.HasValue && Current(_session.Value)) Capture(e);
            }
            catch { _writeFault = true; }
            return;
        }
        if (e.Kind == LifecycleEventKind.SaveSucceeded || e.Kind == LifecycleEventKind.SaveFailed || e.Kind == LifecycleEventKind.SaveSkipped)
        { Complete(e); return; }
        if (!_session.HasValue || !Current(_session.Value)) return;
        if (e.Kind == LifecycleEventKind.PlayerReady && e.Session?.Id == _session) RestoreOwners();
    }

    private void RestoreOwners()
    {
        if (_sessionFault || _restored) return;
        Guid id = _session!.Value;
        try
        {
            if (_loadPath != null)
            {
                if (_hashFile(_loadPath) != _loadHash) throw new InvalidOperationException("Load source changed.");
                var generation = _store.Load(_loadPath, _loadHash!);
                if (generation != null) { _campaign = generation.Identity.Campaign; _known = generation.Owners; }
            }
        }
        catch { _sessionFault = true; return; }
        foreach (var pair in _owners)
        {
            if (!Current(id)) return;
            var owner = pair.Value;
            var result = owner.Codec.Decode(_known.TryGetValue(pair.Key, out var bytes) ? bytes : null);
            if (!Current(id)) return;
            if (result.Status != SchemaReadStatus.Ready && result.Status != SchemaReadStatus.Missing)
            { owner.Status = "schema-" + result.Status; continue; }
            try
            {
                owner.Restore(result.Payload);
                if (!Current(id)) return;
                owner.Ready = true; owner.Status = result.Migrated ? "migration-pending" : "ready";
            }
            catch { owner.Ready = false; owner.Status = "restore-failed"; }
        }
        if (Current(id)) _restored = true;
    }

    private void Capture(LifecycleEvent e)
    {
        if (_sessionFault || !_restored || e.Session?.Id != _session || e.OperationId == null || e.Destination == null) return;
        if (_known.Keys.Union(_owners.Keys, StringComparer.Ordinal).Count() > GenerationStore.MaxOwners)
        { _writeFault = true; _faultDetail = "owner-union-limit"; return; }
        Guid id = _session!.Value;
        var data = _known.ToDictionary(p => p.Key, p => (byte[])p.Value.Clone(), StringComparer.Ordinal);
        bool captureFailed = false;
        foreach (var pair in _owners)
        {
            if (!pair.Value.Ready && pair.Value.Status != "capture-failed") continue; // Inactive owners retain their opaque bytes.
            try
            {
                data[pair.Key] = pair.Value.Codec.Encode(pair.Value.Capture());
                pair.Value.Ready = true; pair.Value.Status = "ready";
            }
            catch { pair.Value.Ready = false; pair.Value.Status = "capture-failed"; captureFailed = true; }
            if (!Current(id)) return;
        }
        // An active owner's failed capture must not label its older bytes as this save's state.
        if (captureFailed) { _writeFault = true; return; }
        try
        {
            if (_pending.ContainsKey(e.OperationId.Value)) throw new InvalidOperationException("Duplicate save start.");
            _pending.Add(e.OperationId.Value, new Pending(id, _campaign, _canonical(e.Destination), data));
        }
        catch { _writeFault = true; }
    }

    private void Complete(LifecycleEvent e)
    {
        if (e.OperationId == null || !_intents.TryGetValue(e.OperationId.Value, out var intent)) return;
        _intents.Remove(e.OperationId.Value);
        _pending.TryGetValue(e.OperationId.Value, out var pending);
        _pending.Remove(e.OperationId.Value);
        try
        {
            if (e.Session?.Id != intent.Session || e.Destination == null || _canonical(e.Destination) != intent.Path)
                throw new InvalidOperationException("Save terminal mismatch.");
            if (e.Kind != LifecycleEventKind.SaveSucceeded)
            {
                // A failed vanilla write may still have changed payload bytes before metadata failed.
                if (e.Kind == LifecycleEventKind.SaveSkipped) _store.ClearIntent(intent.Path, e.OperationId.Value);
                return;
            }
            if (pending == null || !Current(pending.Session)) throw new InvalidOperationException("No publishable candidate.");
            var generation = _store.Publish(pending.Destination, _hashFile(pending.Destination), pending.Campaign, pending.Owners);
            _known = generation.Owners;
            _store.ClearIntent(intent.Path, e.OperationId.Value);
            _writeFault = false; _faultDetail = null;
        }
        catch { _writeFault = true; }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _subscription.Dispose(); Reset(); _disposed = true;
    }
}
