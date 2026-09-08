using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSnapshotPatchTests : IDisposable
{
    public void Dispose() => WorldSnapshotPatches.Host = null;
    private static JsonObject Root() => new() { Text = "snapshot", ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue>()) }) }) };

    [Fact]
    public void SnapshotAndStoreUseTheCapturedHostAndPreserveNativeExceptions()
    {
        var hub = new LifecycleHub((_, error) => throw new Exception("Unexpected fault", error));
        using var host = new WorldSnapshotHookHost(hub, new WorldSnapshotRecorder(new WorldJsonInspection(typeof(JsonObject).Assembly)), () => Array.Empty<WorldSnapshotInstance>(), () => 1);
        WorldSnapshotPatches.Host = host;
        hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(hub.CurrentSession!.Id);
        var root = Root();
        WorldSnapshotPatches.Snapshot.Prefix(out var capture);
        WorldSnapshotPatches.Host = null;
        Assert.Null(WorldSnapshotPatches.Snapshot.Finalizer(capture, root, null));
        WorldSnapshotPatches.Host = host;
        WorldSnapshotPatches.Store.Prefix(root, out var scope);
        Assert.Empty(WorldStateCodec.Decode(host.CaptureOwner(WorldStateCodec.Owner)));
        var error = new IOException("Native write failed");
        Assert.Same(error, WorldSnapshotPatches.Store.Finalizer(scope, error));
        Assert.Throws<InvalidDataException>(() => host.CaptureOwner(WorldStateCodec.Owner));
        WorldSnapshotPatches.Snapshot.Prefix(out capture);
        var failedRoot = Root();
        Assert.Same(error, WorldSnapshotPatches.Snapshot.Finalizer(capture, failedRoot, error));
        Assert.Throws<InvalidDataException>(() => WorldSnapshotPatches.Store.Prefix(failedRoot, out _));
    }
}
