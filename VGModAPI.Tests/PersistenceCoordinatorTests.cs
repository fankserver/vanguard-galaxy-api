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
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
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
        if (terminal == LifecycleEventKind.SaveSkipped) Assert.Null(store.Load("slot", H('a')));
        else Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
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
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('b')));
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
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        var op = BeginSave(); EndSave(op, path: "different");
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void IdenticalByteConflictCannotSilentlyRestoreOlderProgress()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        byte state = 1; int restores = 0;
        coordinator.Register(Codec(), () => new[] { state }, _ => restores++);
        Start(); EndSave(BeginSave());
        state = 2; EndSave(BeginSave());
        Assert.False(coordinator.MutationAllowed("owner"));
        Start(SessionOrigin.SaveLoad, "slot");
        Assert.Equal(1, restores);
        Assert.Equal("load-blocked", coordinator.Status("owner"));
    }

    [Fact]
    public void FailedCaptureThenReloadCannotBecomeEmptySuccess()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        bool fail = false; int restores = 0;
        coordinator.Register(Codec(), () => fail ? throw new Exception("capture") : new byte[] { 1 }, _ => restores++);
        Start(); EndSave(BeginSave());
        fail = true; _hashes["slot"] = H('b'); EndSave(BeginSave());
        Assert.Throws<InvalidDataException>(() => new GenerationStore(_root).Load("slot", H('b')));
        Start(SessionOrigin.SaveLoad, "slot");
        Assert.Equal(1, restores);
        Assert.Equal("load-blocked", coordinator.Status("owner"));
    }

    [Fact]
    public void BlockedLoadSavedToFreshSlotRetainsDurableIntent()
    {
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), new Dictionary<string, byte[]> { ["owner"] = Codec().Encode(new byte[] { 1 }) });
        File.WriteAllText(Directory.GetFiles(_root, "manifest.vgo", SearchOption.AllDirectories)[0], "corrupt");
        using var coordinator = Coordinator(store);
        int restores = 0;
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => restores++);
        Start(SessionOrigin.SaveLoad, "slot");
        _hashes["fresh"] = H('c'); EndSave(BeginSave("fresh"), path: "fresh");
        Start(SessionOrigin.SaveLoad, "fresh");
        Assert.Equal(0, restores);
        Assert.Equal("load-blocked", coordinator.Status("owner"));
    }

    [Fact]
    public void RestoredPlayerReadySaveAndOverlappingSavesAreMatched()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        var session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(session);
        EndSave(BeginSave()); Assert.NotNull(store.Load("slot", H('a')));
        _hub.GameplayInitialized(session);
        _hashes["second"] = H('b');
        var one = BeginSave(); var two = BeginSave("second");
        EndSave(two, path: "second"); Assert.False(coordinator.MutationAllowed("owner"));
        EndSave(one); Assert.True(coordinator.MutationAllowed("owner"));
        Assert.NotNull(store.Load("second", H('b')));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidationOrFailureDiscardsPending(bool failed)
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        var session = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(session);
        var snapshot = _hub.CurrentSession;
        var op = BeginSave();
        if (failed) _hub.Fail(session, "test"); else _hub.Invalidate("test");
        _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, snapshot, op, "slot"));
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void DispatchAndDuplicateStartsNeverGrantMutation()
    {
        var store = new GenerationStore(_root);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        bool observed = false;
        using var sub = _hub.Subscribe("test", e => { if (e.Kind == LifecycleEventKind.SaveSucceeded) { observed = true; Assert.False(coordinator.MutationAllowed("owner")); } });
        Start(); var op = BeginSave();
        _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, _hub.CurrentSession, op, "slot"));
        EndSave(op);
        Assert.True(observed);
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('a')));
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptOrRestoreFailedOwnerIsRetainedButDenied(bool corrupt)
    {
        var store = new GenerationStore(_root);
        var bytes = Codec().Encode(new byte[] { 1 }); if (corrupt) bytes[0] = 0;
        store.Publish("slot", H('a'), Guid.NewGuid(), new Dictionary<string, byte[]> { ["owner"] = bytes });
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => throw new Exception("must not capture"), _ => throw new Exception("restore"));
        coordinator.Register(Codec("healthy"), () => new byte[] { 2 }, _ => { });
        Start(SessionOrigin.SaveLoad, "slot");
        Assert.False(coordinator.MutationAllowed("owner"));
        _hashes["slot"] = H('b'); EndSave(BeginSave());
        Assert.Equal(bytes, store.Load("slot", H('b'))!.Owners["owner"]);
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void MigratedStateIsCandidateOnlyAndCaptureReplacementAbandonsOldSave()
    {
        var store = new GenerationStore(_root);
        store.Publish("slot", H('a'), Guid.NewGuid(), new Dictionary<string, byte[]> { ["owner"] = Codec().Encode(new byte[] { 1 }) });
        using var coordinator = Coordinator(store);
        var newer = new OwnerSchemaCodec("owner", 2, b => b.Length == 1,
            new Dictionary<int, Func<byte[], byte[]>> { [1] = b => new byte[] { 2 } });
        coordinator.Register(newer, () => { _hub.Begin(SessionOrigin.NewGame, null); return new byte[] { 2 }; }, _ => { });
        Start(SessionOrigin.SaveLoad, "slot");
        Assert.Equal("migration-pending", coordinator.Status("owner"));
        Assert.Equal(new byte[] { 1 }, Codec().Decode(store.Load("slot", H('a'))!.Owners["owner"]).Payload);
        _hashes["slot"] = H('b'); var old = _hub.CurrentSession; var op = BeginSave();
        _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, old, op, "slot"));
        Assert.Throws<InvalidDataException>(() => store.Load("slot", H('b')));
        Assert.False(coordinator.MutationAllowed("owner"));
    }

    [Fact]
    public void UnionLimitHasDistinctDiagnostic()
    {
        var store = new GenerationStore(_root);
        var owners = new Dictionary<string, byte[]>();
        for (int i = 0; i < 32; i++) owners.Add("unknown" + i, new byte[] { 1 });
        store.Publish("slot", H('a'), Guid.NewGuid(), owners);
        using var coordinator = Coordinator(store);
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { });
        Start(SessionOrigin.SaveLoad, "slot"); EndSave(BeginSave());
        Assert.Equal("owner-union-limit", coordinator.Status("owner"));
    }

    [Fact]
    public void ReentrantRestoreAndDisposalNeverGrantMutation()
    {
        var coordinator = Coordinator(new GenerationStore(_root));
        int restores = 0;
        coordinator.Register(Codec(), () => new byte[] { 1 }, _ => { if (++restores == 1) _hub.Begin(SessionOrigin.NewGame, null); });
        var old = _hub.Begin(SessionOrigin.NewGame, null); _hub.PlayerReady(old);
        Assert.False(coordinator.MutationAllowed("owner"));
        var replacement = _hub.CurrentSession!.Id;
        Assert.NotEqual(old, replacement);
        _hub.PlayerReady(replacement); _hub.GameplayInitialized(replacement);
        Assert.Equal(2, restores);
        Assert.True(coordinator.MutationAllowed("owner"));
        coordinator.Dispose(); Assert.False(coordinator.MutationAllowed("owner"));
    }
}
