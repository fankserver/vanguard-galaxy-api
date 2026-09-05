using System;
using System.Collections;
using System.IO;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed class GameAdapter
{
    internal readonly LifecycleHub Hub;
    internal readonly SaveTracker Saves;
    internal readonly GameBindings Bindings;
    private readonly Action<Exception> _report;
    private LoadRequest? _request;
    private Guid? _executing;
    private object? _boundPlayer;
    private object? _pendingNewPlayer;
    private volatile bool _faulted;
    private bool _faultReconciled;

    internal GameAdapter(LifecycleHub hub, GameBindings bindings, Action<Exception> report)
    { Hub = hub; Bindings = bindings; Saves = new SaveTracker(hub); _report = report; }

    internal void Guard(Action action)
    {
        if (_faulted) return;
        try { Hub.CheckThread(); action(); }
        catch (Exception ex)
        {
            _faulted = true;
            try { _report(ex); } catch { }
            // Never propagate an observer fault into vanilla, even if a foreign mod
            // invokes a patched method from a worker thread. Reconcile on the next tick.
        }
    }

    internal void Poll()
    {
        if (!_faulted) { Guard(Tick); return; }
        if (_faultReconciled) return;
        _faultReconciled = true;
        Hub.SetCapability("session-lifecycle", false, "Observer fault; see BepInEx log. Restart required.");
        Hub.SetCapability("save-outcomes", false, "Observer fault; see BepInEx log. Restart required.");
        Hub.Invalidate("Observer fault; lifecycle observation stopped.");
    }

    internal LoadRequest BeginLoad(object file)
    {
        var previous = _request;
        var path = ((FileInfo)Bindings.SaveFile.GetValue(file)!).FullName;
        _boundPlayer = null;
        _pendingNewPlayer = null;
        var id = Hub.Begin(SessionOrigin.SaveLoad, path);
        return _request = new LoadRequest(id, previous);
    }

    internal void EndLoadRequest(LoadRequest request, Exception? error)
    {
        _request = request.Previous;
        if (error != null) Hub.Fail(request.Id, "Load request threw " + error.GetType().Name);
        else if (!request.Observed) Hub.Fail(request.Id, "Load coroutine hook was not observed; lifecycle tracking cannot continue.");
    }

    internal IEnumerator ObserveLoad(IEnumerator routine)
    {
        var request = _request;
        if (request == null) return routine; // Do not guess ownership for bypass paths.
        request.Observed = true;
        var id = request.Id;
        return new ObservedEnumerator(routine, () => Enter(id),
            ex => Guard(() => Hub.Fail(id, "Load failed or canceled: " + ex.GetType().Name)),
            () => Guard(() =>
            {
                if (Hub.CurrentSession?.Id == id && Hub.CurrentSession.Phase == SessionPhase.Starting)
                    Hub.Fail(id, "Load iterator ended without player readiness (including rejected versions).");
            }));
    }

    private IDisposable Enter(Guid id)
    {
        var previous = _executing;
        _executing = id;
        return new Scope(() => _executing = previous);
    }

    internal void LoadFailed()
    {
        if (_executing.HasValue) Hub.Fail(_executing.Value, "Vanilla reported a save-load failure.");
    }

    internal Guid BeginNewPlayer()
    {
        _boundPlayer = null;
        _pendingNewPlayer = null;
        return Hub.Begin(SessionOrigin.NewGame, null);
    }

    internal void EndNewPlayer(Guid id, Exception? error)
    {
        if (Hub.CurrentSession?.Id != id || Hub.CurrentSession.Phase != SessionPhase.Starting) return;
        if (error != null) { Hub.Fail(id, "New player creation threw " + error.GetType().Name); return; }
        _pendingNewPlayer = Bindings.CurrentPlayer;
        if (_pendingNewPlayer == null) Hub.Fail(id, "New player creation returned without a player.");
    }

    internal Guid? PlayerReconstructed()
    {
        var current = Hub.CurrentSession;
        var id = _executing ?? (current?.Origin == SessionOrigin.NewGame ? current.Id : (Guid?)null);
        if (id == null || current?.Id != id || current.Phase != SessionPhase.Starting) return null;
        var player = Bindings.CurrentPlayer;
        if (current.Origin == SessionOrigin.NewGame)
        {
            if (_pendingNewPlayer == null) return null;
            if (!ReferenceEquals(_pendingNewPlayer, player))
            {
                Invalidate("Player identity changed before new-game scene initialization.");
                return null;
            }
        }
        if (player == null) { Hub.Fail(id.Value, "Scene loading requested without a player."); return null; }
        _boundPlayer = player;
        _pendingNewPlayer = null;
        Hub.PlayerReady(id.Value);
        return id;
    }

    internal Guid? CaptureGameplay()
    {
        var session = Hub.CurrentSession;
        return session?.Phase == SessionPhase.PlayerReady && _boundPlayer != null && ReferenceEquals(_boundPlayer, Bindings.CurrentPlayer)
            ? session.Id : (Guid?)null;
    }

    internal void GameplayCompleted(Guid id, object manager, Exception? error)
    {
        if (error != null) { Hub.Fail(id, "Gameplay initialization threw " + error.GetType().Name); return; }
        if (ReferenceEquals(_boundPlayer, Bindings.CurrentPlayer) && (bool)Bindings.Initialized.GetValue(manager)!)
            Hub.GameplayInitialized(id);
    }

    internal void Invalidate(string reason)
    { _boundPlayer = null; _pendingNewPlayer = null; Hub.Invalidate(reason); }

    internal void Tick()
    {
        // Unknown player replacements invalidate; they do not manufacture readiness.
        if ((_boundPlayer != null && !ReferenceEquals(_boundPlayer, Bindings.CurrentPlayer))
            || (_pendingNewPlayer != null && !ReferenceEquals(_pendingNewPlayer, Bindings.CurrentPlayer)))
            Invalidate("Player identity changed outside the tracked initialization boundary.");
    }

    internal SessionSnapshot? SaveSession()
    {
        var session = Hub.CurrentSession;
        return _boundPlayer != null && ReferenceEquals(_boundPlayer, Bindings.CurrentPlayer)
            && (session?.Phase == SessionPhase.PlayerReady || session?.Phase == SessionPhase.GameplayInitialized) ? session : null;
    }
    internal string Destination(string name) => Path.GetFullPath(Path.Combine((string)Bindings.SavesPath.GetValue(null)!, name + ".save"));

    internal sealed class LoadRequest
    {
        internal readonly Guid Id;
        internal readonly LoadRequest? Previous;
        internal bool Observed;
        internal LoadRequest(Guid id, LoadRequest? previous) { Id = id; Previous = previous; }
    }
    private sealed class Scope : IDisposable
    {
        private Action? _exit;
        internal Scope(Action exit) => _exit = exit;
        public void Dispose() { var exit = _exit; _exit = null; exit?.Invoke(); }
    }
}
