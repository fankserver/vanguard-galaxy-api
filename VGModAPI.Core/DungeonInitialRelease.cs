using System;

namespace VGModAPI.Core;

/// <summary>Release ordering after all restored pod membership and identity bindings exist.</summary>
internal static class DungeonInitialRelease
{
    internal static void Run(bool activeLocation, Action restoreDocking, Action register, Action observe, Action activate)
    {
        if (activeLocation) restoreDocking();
        register();
        observe();
        activate();
    }
}
