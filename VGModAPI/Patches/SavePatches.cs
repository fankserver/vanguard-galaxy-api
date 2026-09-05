using System;
using VGModAPI.Core;
using VGModAPI.Runtime;

namespace VGModAPI.Patches;

internal static class SavePatches
{
    internal static GameAdapter? Adapter;
    internal static class Store
    {
        internal static void Prefix(object[] __args, out SaveTracker.Call? __state)
        {
            SaveTracker.Call? state = null;
            Adapter?.Guard(() =>
            {
                var player = Adapter.Bindings.CurrentPlayer;
                bool skipped = player != null && (bool)Adapter.Bindings.Ephemeral.GetValue(player)!;
                state = Adapter.Saves.Enter(__args[0], Adapter.Destination((string)__args[1]), __args[2], (int)__args[3], skipped, Adapter.SaveSession());
            });
            __state = state;
        }
        internal static Exception? Finalizer(SaveTracker.Call? __state, Exception? __exception)
        {
            if (__state != null) Adapter?.Guard(() => Adapter.Saves.Exit(__state, __exception));
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
