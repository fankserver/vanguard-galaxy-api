using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PersistenceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vg-service-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static PersistenceProvider Provider(Action<SessionSnapshot, byte[]?>? restore = null, Func<byte[]>? capture = null)
        => new("test.owner", 1, capture ?? (() => new byte[] { 1 }), restore ?? ((_, _) => { }), bytes => bytes.Length == 1);

    [Fact]
    public void PublicRegistrationReflectsReadinessAndDisposal()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var service = new PersistenceService(hub, new GenerationStore(_root), s => s, _ => new string('a', 64));
        SessionSnapshot? restored = null;
        var registration = service.Register(Provider((session, bytes) => { restored = session; Assert.Null(bytes); }));
        Assert.False(registration.MutationAllowed);
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id);
        Assert.Equal(id, restored!.Id); Assert.False(registration.MutationAllowed);
        hub.GameplayInitialized(id); Assert.True(registration.MutationAllowed);
        registration.Dispose(); Assert.False(registration.MutationAllowed); Assert.Equal("inactive", registration.Status);
        Assert.Throws<InvalidOperationException>(() => service.Register(Provider()));
    }

    [Fact]
    public void RemovalDuringCaptureAbandonsCandidateWithoutEnumeratorFailure()
    {
        int errors = 0;
        var hub = new LifecycleHub((_, _) => errors++);
        var store = new GenerationStore(_root);
        using var service = new PersistenceService(hub, store, s => s, _ => new string('a', 64));
        IPersistenceRegistration? registration = null;
        registration = service.Register(Provider(capture: () => { registration!.Dispose(); return new byte[] { 1 }; }));
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        var op = Guid.NewGuid();
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, op, "slot"));
        hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, op, "slot"));
        Assert.Equal(0, errors);
        Assert.Throws<InvalidDataException>(() => store.Load("slot", new string('a', 64)));
    }

    [Fact]
    public void RegistrationIsThreadBoundAndOldDisposedHandleCannotRemoveReplacement()
    {
        var hub = new LifecycleHub((_, _) => { });
        using var service = new PersistenceService(hub, new GenerationStore(_root), s => s, _ => new string('a', 64));
        var old = service.Register(Provider()); old.Dispose();
        var current = service.Register(Provider()); old.Dispose();
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => { _ = current.MutationAllowed; }));
        thread.Start(); thread.Join();
        Assert.IsType<InvalidOperationException>(error);
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        Assert.True(current.MutationAllowed);
        service.Dispose(); Assert.False(current.MutationAllowed);
        Assert.Throws<ObjectDisposedException>(() => service.Register(Provider()));
    }

    [Fact]
    public void ProviderCopiesMigrationRegistry()
    {
        var migrations = new Dictionary<int, Func<byte[], byte[]>> { [1] = b => b };
        var provider = new PersistenceProvider("owner", 2, () => new byte[] { 1 }, (_, _) => { }, _ => true, migrations);
        migrations.Clear(); Assert.Single(provider.Migrations);
    }

    [Fact]
    public void FileAdapterRestrictsCanonicalSaveRootAndHashesBytes()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "test.save"); File.WriteAllBytes(path, new byte[] { 1, 2 });
        var adapter = new PersistenceFiles(_root);
        Assert.Equal(GenerationStore.Hash(new byte[] { 1, 2 }), adapter.HashFile(path));
        Assert.Throws<ArgumentException>(() => adapter.Canonical("relative.save"));
        Assert.Throws<IOException>(() => adapter.Canonical(Path.Combine(_root, "..", "outside.save")));
        Assert.Throws<IOException>(() => adapter.Canonical(Path.Combine(_root, "test.meta")));
        Assert.Throws<IOException>(() => adapter.Canonical(Path.Combine(_root, "TEST~1.save")));
        var link = Path.Combine(_root, "link.save"); File.CreateSymbolicLink(link, path);
        try { Assert.Throws<IOException>(() => adapter.HashFile(link)); }
        finally { File.Delete(link); }
    }
}
