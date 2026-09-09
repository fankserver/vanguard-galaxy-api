using System;
using System.IO;
using System.Reflection;

namespace VGModAPI.Core.Integration;

internal interface IWorldLoadHookHost
{
    bool TryRecall(object file, out object? result);
    void RequireFactory(object value);
}

/// <summary>Load-only host. A disposed host must remain attached as a reserved-content refusal guard.</summary>
internal sealed partial class WorldLoadHookHost : IWorldLoadHookHost, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PersistenceService _persistence;
    private readonly WorldJsonInspection _json;
    private readonly WorldConstructionGate _gate;
    private readonly WorldLoadPreparation _preparation;
    private readonly FieldInfo _file;
    private readonly Func<string, string> _canonical;
    private readonly Func<WorldSavedDefinition, bool> _definitionAvailable;
    private readonly Func<long> _providerRevision;
    private readonly IDisposable _subscription;
    private bool _disposed;
    private Guid _sessionId;

    internal WorldLoadHookHost(Assembly assembly, LifecycleHub hub, PersistenceService persistence, WorldGenerationReader generations,
        Func<string, string> canonical, Func<WorldSavedDefinition, bool> definitionAvailable, Func<long> providerRevision, Func<object, Action, object>? ownedReader = null, bool emptyProfile = false)
    {
        _hub = hub; _hub.CheckThread();
        if (_hub.CurrentSession != null) throw new InvalidOperationException("World load guard must attach before a session.");
        _persistence = persistence; _canonical = canonical; _definitionAvailable = definitionAvailable; _providerRevision = providerRevision;
        _file = assembly.GetType(BindingCatalog.File, true)!.GetField("File") ?? throw new MissingFieldException("SaveGameFile.File");
        if (_file.FieldType != typeof(FileInfo) || _file.IsStatic) throw new MissingFieldException("SaveGameFile.File must be instance FileInfo.");
        _ownedReader = ownedReader ?? ((json, require) => new WorldOwnedPoiReader(assembly).Read(json, require));
        _json = new WorldJsonInspection(assembly, emptyProfile); _gate = new WorldConstructionGate();
        _preparation = new WorldLoadPreparation(generations, _json, _gate);
        _subscription = _hub.Subscribe(WorldStateCodec.Owner, OnLifecycle);
    }

    private WorldPreparedLoad? _prepared;
    private bool _recallAttempted;
    private bool _recallRejected;

    internal WorldPreparedLoad? PreparedFor(Guid session)
    {
        _hub.CheckThread();
        var prepared = _prepared;
        if (_disposed || _factoryRejected || prepared == null) return null;
        long revision = _providerRevision();
        if (!IsCurrent()) return null;
        // Staged loading can yield after construction; readiness must retain the original assets.
        try { prepared.ValidateAssets(); }
        catch
        {
            if (!IsCurrent()) return null;
            _factoryRejected = true;
            _gate.Invalidate();
            throw;
        }
        return IsCurrent() ? prepared : null;

        bool IsCurrent()
        {
            var current = _hub.CurrentSession;
            return !_disposed && !_factoryRejected && ReferenceEquals(prepared, _prepared) && _sessionId == session && prepared.Session == session && current?.Id == session &&
                (current.Phase == SessionPhase.Starting || current.Phase == SessionPhase.PlayerReady || current.Phase == SessionPhase.GameplayInitialized) &&
                revision == prepared.ProviderRevision;
        }
    }

    private void OnLifecycle(LifecycleEvent e)
    {
        if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
        { _prepared = null; _recallAttempted = false; _recallRejected = false; ResetFactories(); _sessionId = e.Session!.Id; _gate.Start(_sessionId); }
        else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _sessionId)
        { _prepared = null; _gate.Invalidate(); }
    }

    public bool TryRecall(object file, out object? result)
    {
        _hub.CheckThread(); result = null;
        if (_disposed || _hub.CurrentSession is not { } session || !_persistence.TryGetStartingLoad(session.Id, out var path, out var hash)) return false;
        _prepared = null;
        if (_recallAttempted) { _recallRejected = true; _gate.Invalidate(); throw new InvalidDataException("World Recall already attempted for this session."); }
        _recallAttempted = true;
        var nativeFile = _file.GetValue(file) as FileInfo ?? throw new InvalidDataException("Missing native load file.");
        if (_canonical(nativeFile.FullName) != path) throw new InvalidDataException("Recall source does not match the observed attempt.");
        bool StillStarting() => !_disposed && !_recallRejected && _persistence.TryGetStartingLoad(session.Id, out var currentPath, out var currentHash) && currentPath == path && currentHash == hash;
        var prepared = _preparation.ReadPrepared(session.Id, path!, hash!, StillStarting, _definitionAvailable, _providerRevision);
        if (!StillStarting()) throw new InvalidDataException("World Recall was invalidated during preparation.");
        _prepared = prepared;
        result = prepared.Root;
        return true;
    }

    public void RequireFactory(object value) => AdmitFactory(value);

    private WorldConstructionNode? AdmitFactory(object value)
    {
        _hub.CheckThread();
        var session = _hub.CurrentSession;
        if (_disposed || session?.Phase != SessionPhase.Starting) _gate.Invalidate();
        long revision = _disposed ? -1 : _providerRevision();
        var current = _hub.CurrentSession;
        if (_disposed || current?.Phase != SessionPhase.Starting || current?.Id != session?.Id)
            _gate.Invalidate();
        return _json.RequireFactory(_gate, current?.Id ?? Guid.Empty, value, revision);
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true; _prepared = null; _gate.Invalidate(); _subscription.Dispose();
    }
}
