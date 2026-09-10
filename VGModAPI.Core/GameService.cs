using System;
using System.Collections.Generic;

namespace VGModAPI.Core;

internal sealed class GameService : IGameService, IDisposable
{
    private readonly LifecycleHub _hub;
    private readonly NavigationService _navigation;
    private readonly InventoryService _inventories;
    private readonly StoryContentService _story;
    private readonly IDisposable _lifetime;
    private readonly List<Handler> _handlers = new();
    private Game? _game;
    private bool _disposed;
    internal GameService(LifecycleHub hub, NavigationService navigation, InventoryService inventories, StoryContentService story)
    {
        _hub = hub; _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _inventories = inventories ?? throw new ArgumentNullException(nameof(inventories));
        _story = story ?? throw new ArgumentNullException(nameof(story));
        _lifetime = hub.Subscribe("vgmodapi.games", OnLifecycle);
        hub.Services.AfterStopped(Dispose);
    }
    public IGame? Current
    { get { _hub.CheckThread(); return !_disposed && _game?.IsActive == true ? _game : null; } }
    public event Action<IGame>? Started
    {
        add
        {
            _hub.CheckThread();
            if (_disposed) throw new ObjectDisposedException(nameof(GameService));
            if (value != null) foreach (Action<IGame> callback in value.GetInvocationList()) _handlers.Add(new Handler(callback));
        }
        remove
        {
            _hub.CheckThread();
            if (value == null) return;
            var removed = value.GetInvocationList();
            for (var start = _handlers.Count - removed.Length; start >= 0; start--)
            {
                var matches = true;
                for (var index = 0; index < removed.Length; index++)
                    if (!_handlers[start + index].Callback.Equals(removed[index])) { matches = false; break; }
                if (!matches) continue;
                for (var index = 0; index < removed.Length; index++) _handlers[start + index].Active = false;
                _handlers.RemoveRange(start, removed.Length); return;
            }
        }
    }
    private void OnLifecycle(LifecycleEvent fact)
    {
        if (fact.Kind != LifecycleEventKind.GameplayInitialized || fact.Session == null ||
            _hub.CurrentSession?.Id != fact.Session.Id || _disposed) return;
        var game = new Game(this, _hub, fact.Session.Id, _navigation, _inventories, _story);
        _game = game;
        foreach (var handler in _handlers.ToArray())
            _hub.Gameplay.Enqueue(fact.Session.Id, handler.Callback.Method.Module.Assembly.GetName().Name ?? "game subscriber",
                () => handler.Callback(game), () => !_disposed && handler.Active);
    }
    public void Dispose()
    {
        _hub.CheckThread(); if (_disposed) return;
        _disposed = true; _lifetime.Dispose(); _game = null;
        foreach (var handler in _handlers) handler.Active = false;
        _handlers.Clear();
    }
    private sealed class Handler
    {
        internal readonly Action<IGame> Callback;
        internal bool Active = true;
        internal Handler(Action<IGame> callback) { Callback = callback; }
    }
    private sealed class Game : IGame
    {
        private readonly GameService _owner;
        private readonly LifecycleHub _hub;
        private readonly Guid _session;
        private readonly INavigation _navigation;
        private readonly IInventories _inventories;
        private readonly IStory _story;
        internal Game(GameService owner, LifecycleHub hub, Guid session, NavigationService navigation, InventoryService inventories, StoryContentService story)
        { _owner = owner; _hub = hub; _session = session; _navigation = navigation.ForGame(session); _inventories = inventories.ForGame(this, session); _story = story.ForGame(this, session, hub); }
        public bool IsActive
        { get { _hub.CheckThread(); return !_owner._disposed && !_hub.Services.IsStopping && _hub.SessionTracking.Availability.IsAvailable && _hub.CurrentSession?.Id == _session && _hub.CurrentSession.Phase == SessionPhase.GameplayInitialized; } }
        public INavigation Navigation { get { _hub.CheckThread(); return _navigation; } }
        public IInventories Inventories { get { _hub.CheckThread(); return _inventories; } }
        public IStory Story { get { _hub.CheckThread(); return _story; } }
    }
}
