using System;
using VGModAPI.Core;

namespace VGModAPI.Patches;

internal static class CraftingCommandPatches
{
    internal static CraftingCommandService? Service;
    internal static void Prefix(out CraftingCommandService? __state)
    {
        __state = null; var service = Service; if (service == null) return;
        try { service.BeginSerialization(); __state = service; }
        catch (Exception error) { service.RecordFault(error); }
    }
    internal static Exception? Finalizer(CraftingCommandService? __state, Exception? __exception)
    {
        try { __state?.EndSerialization(); }
        catch (Exception error) { __state?.RecordFault(error); }
        return __exception;
    }
}
