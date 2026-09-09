using System;
using VGModAPI.Core;

namespace VGModAPI.Patches;
internal static class InventoryPatches
{
    internal static InventoryService? Service;
    internal static void Prefix(out InventoryService? __state)
    {
        __state = null; var service = Service;
        if (service == null) return;
        service.BeginSerialization(); __state = service;
    }
    internal static Exception? Finalizer(InventoryService? __state, Exception? __exception)
    { __state?.EndSerialization(); return __exception; }
}
