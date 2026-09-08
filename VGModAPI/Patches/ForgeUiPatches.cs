using System;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class ForgeUiPatches
{
    internal static ForgeUiRuntime? Runtime;
    internal static Exception? Finalizer(Exception? __exception)
    {
        Runtime?.Observe(__exception);
        return __exception;
    }
}
