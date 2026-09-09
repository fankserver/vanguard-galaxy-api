using System;
using VGModAPI.Core.Integration;

namespace VGModAPI.Patches;

internal static class WorldSnapshotPatches
{
    internal static WorldSnapshotHookHost? Host = null;
    internal sealed class Capture
    {
        internal readonly WorldSnapshotHookHost Host;
        internal readonly object Token;
        internal Capture(WorldSnapshotHookHost host, object token) { Host = host; Token = token; }
    }
    internal static class Snapshot
    {
        internal static void Prefix(out Capture? __state)
        {
            __state = null;
            var host = Host;
            if (host != null) __state = new Capture(host, host.BeginSnapshot());
        }
        internal static Exception? Finalizer(Capture? __state, object __result, Exception? __exception)
        {
            if (__exception == null && __state != null) __state.Host.CompleteSnapshot(__state.Token, __result);
            return __exception;
        }
    }
    internal static class Store
    {
        internal static void Prefix(object data, out IDisposable? __state)
        {
            __state = null;
            __state = Host?.BeginStore(data);
        }
        internal static Exception? Finalizer(IDisposable? __state, Exception? __exception)
        {
            try { __state?.Dispose(); }
            catch when (__exception != null) { return __exception; }
            return __exception;
        }
    }
}
