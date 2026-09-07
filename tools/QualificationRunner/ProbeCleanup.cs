using System;

namespace VGModAPI.Qualification;

internal static class ProbeCleanup
{
    internal static void Run(params Action[] actions)
    {
        Exception? first = null;
        foreach (var action in actions)
        {
            try { action(); }
            catch (Exception error) { first ??= error; }
        }
        if (first != null) throw new InvalidOperationException("Probe cleanup failed after attempting all restoration steps.", first);
    }
}
