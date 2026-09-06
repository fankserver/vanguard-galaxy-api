using System;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class SavePatches
{
    internal static GameAdapter? Adapter;
    internal static class Store
    {
        internal sealed class State
        {
            internal SaveTracker.Call? Save;
            internal MissionSerializationTracker.StoreScope? Identity;
        }
        internal static void Prefix(object[] __args, out State __state)
        {
            var state = new State();
            MissionPatches.Adapter?.Guard(() => state.Identity = MissionPatches.Adapter.BeginIdentityStore(__args[0]));
            Adapter?.Guard(() =>
            {
                var player = Adapter.Bindings.CurrentPlayer;
                bool skipped = player != null && (bool)Adapter.Bindings.Ephemeral.GetValue(player)!;
                state.Save = Adapter.Saves.Enter(__args[0], Adapter.Destination((string)__args[1]), __args[2], (int)__args[3], skipped, Adapter.SaveSession());
            });
            __state = state;
        }
        internal static Exception? Finalizer(State? __state, Exception? __exception)
        {
            try { if (__state?.Save != null) Adapter?.Guard(() => Adapter.Saves.Exit(__state.Save, __exception)); }
            finally { if (__state?.Identity != null) MissionPatches.Adapter?.Guard(__state.Identity.Dispose); }
            return __exception;
        }
    }
    internal static class WriteFile
    {
        internal static void Postfix(object[] __args) => Adapter?.Guard(() =>
            Adapter.Saves.FileWritten(__args[1], ((System.IO.FileInfo)__args[0]).FullName, __args[2]));
    }
    internal static class WriteMetadata
    {
        internal static void Postfix(object[] __args) => Adapter?.Guard(() =>
            Adapter.Saves.MetadataWritten(Adapter.Destination((string)__args[0])));
    }
    internal static class StoreFailure
    {
        internal static void Prefix(object[] __args) => Adapter?.Guard(() =>
            Adapter.Saves.HandlingFailure(__args[0], Adapter.Destination((string)__args[1]), (int)__args[3]));
    }
}
