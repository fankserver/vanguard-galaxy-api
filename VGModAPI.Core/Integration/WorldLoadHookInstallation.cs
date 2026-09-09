using System;

namespace VGModAPI.Core.Integration;

internal static class WorldLoadHookInstallation
{
    internal static void CleanupFailure(Action stop, Action rollback, Action retainRefusalHost, Action<Exception> report)
    {
        bool uncertain = false;
        void Report(Exception error) { try { report(error); } catch { } }
        try { stop(); } catch (Exception error) { uncertain = true; Report(error); }
        try { rollback(); } catch (Exception error) { uncertain = true; Report(error); }
        if (uncertain)
            try { retainRefusalHost(); } catch (Exception error) { Report(error); }
    }

    // No host or authoring capability may be published until both guards are installed.
    internal static void Install(Action factory, Action recall, Action rollback)
    {
        try { factory(); recall(); }
        catch { rollback(); throw; }
    }
}
