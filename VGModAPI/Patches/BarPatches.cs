using System;
using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class BarPatches
{
    internal static IBarHookHost? Host = null;

    internal sealed class RefreshState
    {
        internal readonly IBarHookHost Host;
        internal readonly IBarRefreshScope Scope;
        internal bool Completed;
        internal RefreshState(IBarHookHost host, IBarRefreshScope scope) { Host = host; Scope = scope; }
    }

    internal static class Refresh
    {
        internal static void Prefix(object __instance, out RefreshState? __state)
        {
            __state = null;
            var host = Host;
            if (host == null) return;
            try { __state = new RefreshState(host, host.BeginRefresh(__instance)); }
            catch (Exception error) { Report(host, error); }
        }

        internal static void Finalizer(bool __runOriginal, Exception? __exception, RefreshState? __state)
        {
            if (__state == null || __state.Completed) return;
            __state.Completed = true;
            try { __state.Scope.Complete(__runOriginal, __exception == null); }
            catch (Exception error) { Report(__state.Host, error); }
            // A void finalizer leaves the original exception unchanged.
        }
    }

    internal static class Serialize
    {
        internal static bool Prefix(object __instance, ref object? __result)
        {
            var host = Host;
            if (host == null) return true;
            // A refused content snapshot must fail the save, never fall back to serializing
            // managed contacts as ordinary native salesmen.
            if (!host.TrySerialize(__instance, out var result)) return true;
            __result = result ?? throw new InvalidOperationException("Missing managed bar serialization result.");
            return false;
        }
    }

    internal static class PatronSerialize
    {
        internal static void Prefix(object __instance)
        {
            if (Host?.IsOwned(__instance) == true)
                throw new InvalidOperationException("Managed contacts serialize only through their owning bar and API state.");
        }
    }

    internal static class Interact
    {
        internal static bool Prefix(object __instance)
        {
            var host = Host;
            if (host == null || !host.IsOwned(__instance)) return true;
            try { host.Interact(__instance); }
            catch (Exception error) { Report(host, error); }
            return false;
        }
    }

    private static void Report(IBarHookHost host, Exception error)
    {
        try { host.Fault(error); } catch { }
    }
}
