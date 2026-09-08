using System;

namespace VGModAPI.Core.Integration;

internal interface IBarRefreshScope
{
    void Complete(bool originalRan, bool succeeded);
}

internal interface IBarHookHost
{
    IBarRefreshScope BeginRefresh(object bar);
    bool TrySerialize(object bar, out object? result);
    bool IsOwned(object patron);
    void Interact(object patron);
    void Fault(Exception error);
}
