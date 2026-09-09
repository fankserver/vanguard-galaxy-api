using System;
using System.IO;
using VGModAPI.Runtime;

namespace VGModAPI.Core.Integration;

/// <summary>Publishes reconstructed instance bookkeeping at observed PlayerReady, before dependent content subscribers.</summary>
internal sealed class WorldRuntimeState : IDisposable
{
    private readonly GameAdapter _game;
    private readonly WorldLoadHookHost _loads;
    private readonly WorldDefinitionRegistry _definitions;
    private readonly WorldCreationCoordinator _creation;
    private readonly WorldNativeReconstruction _reconstruction;
    private readonly IDisposable _subscription;
    private readonly WorldLifetimeGuard? _lifetime;
    private readonly Func<Guid, bool>? _persistenceReady;
    private readonly Func<bool>? _allowOwnedRuntime;
    private Guid _session;
    private bool _disposed;
    internal WorldRuntimeState(GameAdapter game, WorldLoadHookHost loads, WorldDefinitionRegistry definitions, WorldCreationCoordinator creation,
        WorldLifetimeGuard? lifetime = null, Func<Guid, bool>? persistenceReady = null, Func<bool>? allowOwnedRuntime = null)
    {
        _lifetime = lifetime; _persistenceReady = persistenceReady; _allowOwnedRuntime = allowOwnedRuntime;
        _game = game; _loads = loads; _definitions = definitions; _creation = creation;
        _game.Hub.CheckThread();
        if (_game.Hub.CurrentSession != null) throw new InvalidOperationException("World restoration must attach before a session.");
        _reconstruction = new WorldNativeReconstruction(game);
        _subscription = game.Hub.Subscribe("vgmodapi.world-state", OnLifecycle);
    }
    private void OnLifecycle(LifecycleEvent e)
    {
        if (_disposed) return;
        if (e.Kind == LifecycleEventKind.SessionStarting && e.Session?.Id == _game.Hub.CurrentSession?.Id)
        { _session = e.Session!.Id; _creation.Reset(_session); }
        else if ((e.Kind == LifecycleEventKind.SessionInvalidated || e.Kind == LifecycleEventKind.SessionStartFailed) && e.Session?.Id == _session)
        { _session = Guid.Empty; _creation.Reset(Guid.Empty); }
        else if (e.Kind == LifecycleEventKind.PlayerReady && e.Session?.Id == _session && _game.Hub.CurrentSession?.Id == _session)
        {
            Guid session = _session;
            long revision = _definitions.Revision;
            bool Current() => !_disposed && _session == session && _game.Hub.CurrentSession?.Id == session && _definitions.Revision == revision;
            if (e.Session!.Origin == SessionOrigin.NewGame)
            {
                // Empty is established by inspecting the actual new map, not by assuming absence.
                var empty = new WorldPreparedLoad(session, new object(), null, revision);
                if (!_creation.TryRestore(session, () => _reconstruction.Read(empty, Current, _ => false, _definitions.Effective)))
                    throw new InvalidDataException("New-game world state could not be established.");
            }
            else
            {
                var prepared = _loads.PreparedFor(session) ?? throw new InvalidDataException("World restoration lacks verified load metadata.");
                if (prepared.ProviderRevision != revision || !_creation.TryRestore(session, () => _reconstruction.Read(prepared,
                    () => Current() && ReferenceEquals(_loads.PreparedFor(session), prepared) && Current(),
                    record => _loads.ConstructedBy(prepared, record), _definitions.Effective)))
                    throw new InvalidDataException("World reconstruction could not be published.");
            }
            if (_lifetime != null && _persistenceReady != null && _allowOwnedRuntime != null)
            {
                var context = _reconstruction.CaptureContext(session);
                bool Available() => Current() && context() && _allowOwnedRuntime() &&
                    _persistenceReady(session) && _creation.HasRestoredInventory(session) && Current() && context();
                // State readiness permits native initialization, not public mutations during callback dispatch.
                // The separate runtime admission gate remains closed until supported behavior is qualified.
                if (Available()) _lifetime.Ready(session, Available);
            }
        }
    }
    public void Dispose()
    {
        _game.Hub.CheckThread(); if (_disposed) return;
        _disposed = true; _session = Guid.Empty; _creation.Reset(Guid.Empty); _subscription.Dispose();
    }
}
