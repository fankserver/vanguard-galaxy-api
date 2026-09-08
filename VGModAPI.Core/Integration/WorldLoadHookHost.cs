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
internal sealed class WorldLoadHookHost : IWorldLoadHookHost, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly PersistenceService _persistence;
    private readonly WorldJsonInspection _json;
    private readonly WorldConstructionGate _gate;
    private readonly WorldLoadPreparation _preparation;
    private readonly FieldInfo _file;
    private readonly Func<string, string> _canonical;
    private readonly Func<WorldSavedObject, bool> _definitionAvailable;
    private readonly Func<long> _providerRevision;
    private readonly IDisposable _subscription;
    private bool _disposed;
    private Guid _sessionId;

    internal WorldLoadHookHost(Assembly assembly, LifecycleHub hub, PersistenceService persistence, GenerationStore store,
        Func<string, string> canonical, Func<WorldSavedObject, bool> definitionAvailable, Func<long> providerRevision)
    {
        _hub = hub; _hub.CheckThread();
        if (_hub.CurrentSession != null) throw new InvalidOperationException("World load guard must attach before a session.");
        _persistence = persistence; _canonical = canonical; _definitionAvailable = definitionAvailable; _providerRevision = providerRevision;
        _file = assembly.GetType(BindingCatalog.File, true)!.GetField("File") ?? throw new MissingFieldException("SaveGameFile.File");
        if (_file.FieldType != typeof(FileInfo) || _file.IsStatic) throw new MissingFieldException("SaveGameFile.File must be instance FileInfo.");
        _json = new WorldJsonInspection(assembly); _gate = new WorldConstructionGate();
        _preparation = new WorldLoadPreparation(new WorldGenerationReader(store), _json, _gate);
        _subscription = _hub.Subscribe(WorldStateCodec.Owner, OnLifecycle);
    }

    private void OnLifecycle(LifecycleEvent e)
    {
        if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _hub.CurrentSession?.Id)
        { _sessionId = e.Session!.Id; _gate.Start(_sessionId); }
        else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _sessionId)
            _gate.Invalidate();
    }

    public bool TryRecall(object file, out object? result)
    {
        _hub.CheckThread(); result = null;
        if (_disposed || _hub.CurrentSession is not { } session || !_persistence.TryGetStartingLoad(session.Id, out var path, out var hash)) return false;
        var nativeFile = _file.GetValue(file) as FileInfo ?? throw new InvalidDataException("Missing native load file.");
        if (_canonical(nativeFile.FullName) != path) throw new InvalidDataException("Recall source does not match the observed attempt.");
        bool StillStarting() => !_disposed && _persistence.TryGetStartingLoad(session.Id, out var currentPath, out var currentHash) && currentPath == path && currentHash == hash;
        result = _preparation.Read(session.Id, path!, hash!, StillStarting, _definitionAvailable, _providerRevision);
        return true;
    }

    public void RequireFactory(object value)
    {
        _hub.CheckThread();
        var session = _hub.CurrentSession;
        if (_disposed || session?.Phase != SessionPhase.Starting) _gate.Invalidate();
        long revision = _disposed ? -1 : _providerRevision();
        var current = _hub.CurrentSession;
        if (_disposed || current?.Phase != SessionPhase.Starting || current?.Id != session?.Id)
            _gate.Invalidate();
        _json.RequireFactory(_gate, current?.Id ?? Guid.Empty, value, revision);
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true; _gate.Invalidate(); _subscription.Dispose();
    }
}
