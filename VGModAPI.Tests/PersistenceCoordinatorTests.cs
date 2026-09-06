using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PersistenceCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-coord-" + Guid.NewGuid().ToString("N"));
    private readonly LifecycleHub _hub = new((_, error) => throw new Exception("Unexpected subscriber fault", error));
    private readonly Dictionary<string, string> _hashes = new() { ["slot"] = H('a') };
    private static string H(char c) => new(c, 64);
    private static OwnerSchemaCodec Codec(string name = "owner", int version = 1) => new(name, version, b => b.Length == 1);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private PersistenceCoordinator Coordinator(GenerationStore store) => new(_hub, store, s => s, s => _hashes[s]);
    private Guid Start(SessionOrigin origin = SessionOrigin.NewGame, string? path = null)
    { var id = _hub.Begin(origin, path); _hub.PlayerReady(id); _hub.GameplayInitialized(id); return id; }
    private Guid BeginSave(string path = "slot")
    { var id = Guid.NewGuid(); _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, _hub.CurrentSession, id, path)); return id; }
    private void EndSave(Guid id, LifecycleEventKind kind = LifecycleEventKind.SaveSucceeded, string path = "slot")
        => _hub.Publish(new LifecycleEvent(kind, _hub.CurrentSession, id, path));

    [Fact]
    public void CapturesAtStartAndCommitsOnlyMatchingSuccess()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        byte state = 1;
        coordinator.Register(Codec(), () => new[] { state }, _ => { });
        Start();
        var op = BeginSave(); state = 2;
        Assert.False(coordinator.MutationAllowed("owner"));
        Assert.Null(store.Load("slot", H('a')));
        EndSave(op);
        Assert.Equal(new byte[] { 1 }, Codec().Decode(store.Load("slot", H('a'))!.Owners["owner"]).Payload);
        Assert.True(coordinator.MutationAllowed("owner"));
    }

    [Theory]
    [InlineData(LifecycleEventKind.SaveFailed)]
    [InlineData(LifecycleEventKind.SaveSkipped)]
    public void FailedOrSkippedWritesNeverPublish(LifecycleEventKind terminal)
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        Start(); var op = BeginSave(); EndSave(op, terminal);
        Assert.Null(store.Load("slot", H('a')));
        Assert.True(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void RollbackRestoresExactGenerationNotFutureState()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        byte state = 1;
        coordinator.Register(Codec(), () => new[] { state }, bytes => { if (bytes != null) state = bytes[0]; });
        Start(); EndSave(BeginSave());
        state = 2; _hashes["slot"] = H('b'); EndSave(BeginSave());
        _hashes["slot"] = H('a'); Start(SessionOrigin.SaveLoad, "slot");
        Assert.Equal(1, state);
        Assert.True(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void ChangedLoadSourceBlocksWithoutEmptyRestoration()
    {
        using var coordinator = Coordinator(new GenerationStore(_root));
        int restores = 0;
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => restores++);
        var id = _hub.Begin(SessionOrigin.SaveLoad, "slot");
        _hashes["slot"] = H('b'); _hub.PlayerReady(id); _hub.GameplayInitialized(id);
        Assert.Equal(0, restores);
        Assert.Equal("load-blocked", coordinator.Status("owner"));
    }

    [Fact]
    public void FutureOwnerIsBlockedAndPreservedWhileAnotherOwnerAdvances()
    {
        var store = new GenerationStore(_root);
        var future = Codec("future", 2).Encode(new byte[] { 9 });
        store.Publish("slot", H('a'), Guid.NewGuid(), new Dictionary<string, byte[]> { ["future"] = future });
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec("future"), () => throw new Exception("must not capture"), _ => throw new Exception("must not restore"));
        coordinator.Register(Codec("healthy"), () => new byte[] { 1 }, _ => { });
        Start(SessionOrigin.SaveLoad, "slot");
        Assert.False(coordinator.MutationAllowed("future")); Assert.True(coordinator.MutationAllowed("healthy"));
        _hashes["slot"] = H('b'); EndSave(BeginSave());
        Assert.Equal(future, store.Load("slot", H('b'))!.Owners["future"]);
    }

    [Fact]
    public void CaptureFailureCannotRelabelOldBytesAndCanRetry()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        bool fail = false;
        coordinator.Register(Codec(), () => fail ? throw new Exception("capture") : new byte[] { 1 }, _ => { });
        Start(); EndSave(BeginSave());
        _hashes["slot"] = H('b'); fail = true; EndSave(BeginSave());
        Assert.Null(store.Load("slot", H('b')));
        Assert.False(coordinator.MutationAllowed("owner"));
        fail = false; EndSave(BeginSave());
        Assert.NotNull(store.Load("slot", H('b')));
        Assert.True(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void PublicationFaultPausesUntilSuccessfulRetry()
    {
        bool fail = true;
        var store = new GenerationStore(_root, _ => { if (fail) throw new IOException("disk fault"); });
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        Start(); EndSave(BeginSave());
        Assert.Equal("publication-blocked", coordinator.Status("owner"));
        Assert.False(coordinator.MutationAllowed("owner"));
        fail = false; EndSave(BeginSave());
        Assert.True(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void ReplacementAndWrongDestinationCannotCommitOldSnapshot()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        Start(); var old = BeginSave(); Start(); EndSave(old);
        Assert.Null(store.Load("slot", H('a')));
        var op = BeginSave(); EndSave(op, path: "different");
        Assert.Null(store.Load("slot", H('a')));
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void ReentrantRestoreAndDisposalNeverGrantMutation()
    {
        var coordinator = Coordinator(new GenerationStore(_root));
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => _hub.Begin(SessionOrigin.NewGame, null));
        var old = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(old);
        Assert.False(coordinator.MutationAllowed("owner"));
        coordinator.Dispose(); Assert.False(coordinator.MutationAllowed("owner"));
    }
}
