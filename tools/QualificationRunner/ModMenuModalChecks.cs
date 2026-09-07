using System;

namespace VGModAPI.Qualification;

internal static class ModMenuModalChecks
{
    internal static void AfterNativeStart(bool open, float timeScale)
    {
        if (!open || timeScale != 0f) throw new InvalidOperationException("Native modal did not retain its inspected paused state after Start.");
    }

    internal static void AfterNativeDestroy(bool open, float timeScale)
    {
        if (open || timeScale != 1f) throw new InvalidOperationException("Native modal did not restore the unpaused menu after destruction.");
    }
}
