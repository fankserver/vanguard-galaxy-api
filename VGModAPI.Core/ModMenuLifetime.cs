using System;

namespace VGModAPI.Core;

internal interface IModMenuView : IDisposable
{
    void Tick(bool modalOpen);
}

// Identity comes from the inspected native menu and its actual viewport/canvas, never session state.
internal sealed class ModMenuLifetime : IDisposable
{
    private readonly Func<object, object, object, IModMenuView> _create;
    private object? _menu, _viewport, _canvas;
    private IModMenuView? _view;
    private bool _disposed;

    internal ModMenuLifetime(Func<object, object, object, IModMenuView> create) => _create = create;

    internal void Poll(object? activeMenu, object? viewport, object? canvas, bool modalOpen)
    {
        if (_disposed) return;
        if (!ReferenceEquals(_menu, activeMenu) || !ReferenceEquals(_viewport, viewport) || !ReferenceEquals(_canvas, canvas))
            Detach();
        if (activeMenu == null || viewport == null || canvas == null) { Detach(); return; }
        if (_view == null)
        {
            // Wait rather than adding/selecting UI behind a native startup/exit popup.
            if (modalOpen) return;
            _view = _create(activeMenu, viewport, canvas);
            _menu = activeMenu; _viewport = viewport; _canvas = canvas;
        }
        _view.Tick(modalOpen);
    }

    private void Detach()
    {
        var view = _view;
        _view = null; _menu = _viewport = _canvas = null;
        view?.Dispose();
    }

    public void Dispose() { _disposed = true; Detach(); }
}

internal static class ModMenuRows
{
    internal const int Height = 88;
    internal static int First(int count, float offset, float viewportHeight)
    {
        var maximum = Math.Max(0, count * (float)Height - Math.Max(0, viewportHeight));
        return Math.Min(Math.Max(0, count - 1), (int)(Math.Max(0, Math.Min(offset, maximum)) / Height));
    }
    internal static int VisibleCount(int count, float viewportHeight) =>
        Math.Min(count, Math.Max(1, (int)Math.Ceiling(Math.Max(0, viewportHeight) / Height) + 1));
}
