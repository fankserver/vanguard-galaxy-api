using System;
using System.Reflection;
using UnityEngine;
using VGModAPI.Core;
using VGModAPI.Unity;

namespace VGModAPI.Runtime;

internal sealed class GameplayUiRuntime : IDisposable
{
    private readonly GameplayUiService _service;
    private readonly FieldInfo _instance;
    private readonly Func<Guid?> _session;
    private readonly Action _checkThread;
    private Surface? _surface;
    private bool _disposed;
    private volatile bool _faulted;

    internal GameplayUiRuntime(GameplayUiService service, Assembly assembly, Func<Guid?> session, Action checkThread)
    {
        _service = service; _session = session; _checkThread = checkThread;
        _instance = assembly.GetType(GameplayUiBindings.PanelType, true)!.GetField("instance")!;
        GameplayUiExtensions.Bind(service, CreateContainer);
    }

    internal sealed class StartAttempt
    {
        internal readonly GameplayUiRuntime Owner;
        internal readonly Guid Session;
        internal bool RanOriginal;
        internal StartAttempt(GameplayUiRuntime owner, Guid session) { Owner = owner; Session = session; }
    }

    internal StartAttempt? Starting()
    {
        StartAttempt? result = null;
        Guard(() => { if (_session() is { } session) result = new StartAttempt(this, session); });
        return result;
    }

    internal void Started(Component panel, StartAttempt attempt)
    {
        Guard(() =>
        {
            if (!ReferenceEquals(attempt.Owner, this) || !attempt.RanOriginal || _session() != attempt.Session || panel == null ||
                _instance.GetValue(null) as Component != panel) return;
            if (_surface?.Panel == panel && _service.Current != null) return;
            // Start has completed native RectTransform and idle status initialization. Hidden UI still exists.
            var canvas = panel.GetComponentInParent<Canvas>(true)?.rootCanvas;
            if (canvas == null || canvas.renderMode == RenderMode.WorldSpace) return;
            var surface = new Surface(this, panel, canvas, attempt.Session);
            _surface = surface;
            _service.Observe(attempt.Session, surface);
        });
    }

    internal void Reconcile() => Guard(_service.Refresh);
    internal void Tick()
    {
        if (_disposed) return;
        _service.Refresh();
    }
    private void Guard(Action action)
    {
        if (_disposed || _faulted) return;
        try { _checkThread(); action(); }
        catch (Exception error)
        {
            _faulted = true;
            _service.LatchFault(error);
            // A foreign-thread hook cannot deliver callbacks. Tick publishes the fault on the main thread.
        }
    }

    private GameplayUiContainerStatus CreateContainer(GameplayUiSnapshot host, string pluginId, string localId, out GameplayUiContainer? container)
    {
        _checkThread(); container = null;
        if (_disposed || _faulted) return GameplayUiContainerStatus.Unavailable;
        var status = _service.CreateContainer(host, pluginId, localId, out var lease);
        if (lease != null)
            container = new GameplayUiContainer(host, pluginId, localId,
                () => (lease.Resource as ContainerResource)?.Root, lease.Dispose);
        return status;
    }

    public void Dispose()
    {
        _checkThread(); if (_disposed) return;
        _disposed = true;
        GameplayUiExtensions.Unbind(_service);
        _service.SetAvailable(false);
        _surface?.Dispose(); _surface = null;
    }

    private sealed class Surface : IGameplayUiSurface
    {
        private readonly GameplayUiRuntime _owner;
        internal readonly Component Panel;
        private readonly Canvas _canvas;
        private readonly Guid _session;
        private GameplayUiLifetime? _panelWatch, _canvasWatch;
        private bool _disposed;
        internal Surface(GameplayUiRuntime owner, Component panel, Canvas canvas, Guid session)
        {
            _owner = owner; Panel = panel; _canvas = canvas; _session = session;
            try
            {
                _panelWatch = Watch(panel.gameObject);
                _canvasWatch = Watch(canvas.gameObject);
            }
            catch { Dispose(); throw; }
        }
        private GameplayUiLifetime Watch(GameObject target)
        {
            var watch = target.AddComponent<GameplayUiLifetime>();
            watch.Destroyed = () => _owner.Guard(() => _owner._service.Invalidate(this));
            watch.HierarchyChanged = _owner.Reconcile;
            return watch;
        }
        public bool IsAlive => !_disposed && !_owner._disposed && !_owner._faulted && Panel != null && _canvas != null &&
            _owner._session() == _session && _owner._instance.GetValue(null) as Component == Panel &&
            Panel.GetComponentInParent<Canvas>(true)?.rootCanvas == _canvas && _canvas.renderMode != RenderMode.WorldSpace;

        public IGameplayUiContainerResource CreateContainer(string pluginId, string localId)
        {
            GameObject? root = null;
            try
            {
                root = new GameObject("VGModAPI.UI." + pluginId + "." + localId, typeof(RectTransform));
                root.layer = _canvas.gameObject.layer;
                var rect = (RectTransform)root.transform;
                rect.SetParent(_canvas.transform, false);
                rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero; rect.offsetMax = Vector2.zero;
                return new ContainerResource(rect, _canvas);
            }
            catch
            {
                if (root != null) { root.SetActive(false); UnityEngine.Object.Destroy(root); }
                throw;
            }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Release(_panelWatch); Release(_canvasWatch);
            _panelWatch = null; _canvasWatch = null;
            if (ReferenceEquals(_owner._surface, this)) _owner._surface = null;
        }
        private static void Release(GameplayUiLifetime? watch)
        {
            if (watch == null) return;
            watch.Destroyed = null; watch.HierarchyChanged = null;
            UnityEngine.Object.Destroy(watch);
        }
    }

    private sealed class ContainerResource : IGameplayUiContainerResource
    {
        private RectTransform? _root;
        private readonly Canvas _canvas;
        internal ContainerResource(RectTransform root, Canvas canvas) { _root = root; _canvas = canvas; }
        public bool IsAlive => _root != null && _canvas != null && _root.parent == _canvas.transform;
        internal RectTransform? Root => IsAlive ? _root : null;
        public void Dispose()
        {
            var root = _root; _root = null;
            if (root == null) return;
            try { root.gameObject.SetActive(false); }
            finally { UnityEngine.Object.Destroy(root.gameObject); }
        }
    }
}

/// <summary>Lifetime observation only: deactivation/hiding is deliberately not teardown.</summary>
internal sealed class GameplayUiLifetime : MonoBehaviour
{
    internal Action? Destroyed;
    internal Action? HierarchyChanged;
    private void OnDestroy() { var callback = Destroyed; Destroyed = null; HierarchyChanged = null; callback?.Invoke(); }
    private void OnTransformParentChanged() => HierarchyChanged?.Invoke();
}
