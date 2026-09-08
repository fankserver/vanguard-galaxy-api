using System;

namespace VGModAPI.Core.Integration;

internal static class WorldLoadHookInstallation
{
    // No host or authoring capability may be published until both guards are installed.
    internal static void Install(Action factory, Action recall, Action rollback)
    {
        try { factory(); recall(); }
        catch { rollback(); throw; }
    }
}
