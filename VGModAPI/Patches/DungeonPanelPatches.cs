using System;
using VGModAPI.Runtime;
namespace VGModAPI.Patches;

internal static class DungeonPanelPatches
{
    internal static DungeonPanelRuntime? Runtime { get; set; }
    internal static Action<Exception>? Report { get; set; }
    private static void Observe(Action action)
    { try { action(); } catch (Exception error) { try { Report?.Invoke(error); } catch { } } }
    internal static class Opened
    { internal static void Postfix(object __instance) => Observe(() => Runtime?.Opened(__instance)); }
    internal static class Closed
    { internal static void Postfix(object __instance) => Observe(() => Runtime?.Closed(__instance)); }
}
