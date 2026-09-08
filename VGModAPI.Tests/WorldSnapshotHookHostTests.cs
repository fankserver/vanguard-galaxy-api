using System;
using System.Collections.Generic;
using System.IO;
using LightJson;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldSnapshotHookHostTests
{
    private static JsonObject Root(string text) => new() { Text = text, ["Version"] = new("0.8.2.3"), ["Player"] = new(new JsonObject { ["map"] = new(new JsonObject { ["systems"] = new(new List<JsonValue>()) }) }) };
    private static LifecycleHub Hub() => new((_, error) => throw new Exception("Unexpected lifecycle fault", error));
    private static void Ready(LifecycleHub hub) { hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(hub.CurrentSession!.Id); }
    private static WorldSnapshotHookHost Host(LifecycleHub hub, Func<IReadOnlyList<WorldSnapshotInstance>>? source = null) =>
        new(hub, new WorldSnapshotRecorder(new WorldJsonInspection(typeof(JsonObject).Assembly)), source ?? (() => Array.Empty<WorldSnapshotInstance>()), () => 1);

    [Fact]
    public void StoreScopesAreNestedAndOldDisposalCannotCloseANewSessionScope()
    {
        var hub = Hub(); using var host = Host(hub); Ready(hub);
        var root = Root("snapshot"); host.CompleteSnapshot(host.BeginSnapshot(), root);
        var outer = host.BeginStore(root); var inner = host.BeginStore(root);
        Assert.Empty(WorldStateCodec.Decode(host.CaptureOwner(WorldStateCodec.Owner)));
        inner.Dispose(); Assert.Empty(WorldDefinitionCodec.Decode(host.CaptureOwner(WorldDefinitionCodec.Owner)));
        hub.Invalidate("new session"); Ready(hub);
        host.CompleteSnapshot(host.BeginSnapshot(), root);
        using var current = host.BeginStore(root);
        outer.Dispose(); Assert.Empty(WorldStateCodec.Decode(host.CaptureOwner(WorldStateCodec.Owner)));
        current.Dispose(); Assert.Throws<InvalidDataException>(() => host.CaptureOwner(WorldStateCodec.Owner));
    }

    [Fact]
    public void SourceCallbackCannotCarrySnapshotWorkAcrossSessionInvalidation()
    {
        var hub = Hub(); bool invalidate = false;
        using var host = Host(hub, () =>
        {
            if (invalidate) hub.Invalidate("source invalidated session");
            return Array.Empty<WorldSnapshotInstance>();
        });
        Ready(hub); var token = host.BeginSnapshot(); invalidate = true;
        Assert.Throws<InvalidDataException>(() => host.CompleteSnapshot(token, Root("refused")));
        Assert.Throws<InvalidDataException>(() => host.BeginStore(Root("refused")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalRevisionValidationCannotPublishOnFailure(bool throws)
    {
        var hub = Hub(); int calls = 0; bool completing = false;
        using var host = new WorldSnapshotHookHost(hub,
            new WorldSnapshotRecorder(new WorldJsonInspection(typeof(JsonObject).Assembly)),
            () => Array.Empty<WorldSnapshotInstance>(), () =>
            {
                if (completing && ++calls == 3)
                {
                    if (throws) throw new InvalidDataException("Final revision callback failed");
                    return 2;
                }
                return 1;
            });
        Ready(hub); var root = Root("failed-final-validation");
        var token = host.BeginSnapshot(); completing = true;
        Assert.Throws<InvalidDataException>(() => host.CompleteSnapshot(token, root));
        Assert.Throws<InvalidDataException>(() => host.BeginStore(root));
    }

    [Fact]
    public void UnassociatedStoresAndDisposedHostsRefuse()
    {
        var hub = Hub(); using var host = Host(hub); Ready(hub);
        Assert.Throws<InvalidDataException>(() => host.BeginStore(Root("unknown")));
        var token = host.BeginSnapshot(); host.Dispose();
        Assert.Throws<InvalidDataException>(() => host.CompleteSnapshot(token, Root("old")));
        Assert.Throws<InvalidDataException>(() => host.CaptureOwner(WorldStateCodec.Owner));
    }
}
