using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;
using NativeObject = VGModAPI.Tests.DungeonLayoutBuilderTests.NativeObject;

namespace VGModAPI.Tests;

public sealed class DungeonPodResumeAdapterTests
{
    private sealed class Persistence : IPersistenceApi, IPersistenceRegistration, IPersistenceReadiness
    {
        internal PersistenceProvider Provider = null!;
        public bool MutationAllowed => true;
        public bool StateReady => true;
        public string Status => "test";
        public IPersistenceRegistration Register(PersistenceProvider provider) { Provider = provider; return this; }
        public void Dispose() { }
    }
    [Fact]
    public void RestoredManifestIsNotReplacedByNativeDefaultAndMissingSavedStateIsRejected()
    {
        using var hub = new LifecycleHub((_, _) => { }); var persistence = new Persistence(); using var pods = new DungeonPodPersistence(hub, persistence);
        var session = hub.Begin(SessionOrigin.SaveLoad, "save"); hub.PlayerReady(session); persistence.Provider.Restore(hub.CurrentSession!, null);
        var adapter = new DungeonPodResumeAdapter(pods, new DungeonLayoutBuilderTests.Native());
        var data = new NativeObject(); data.Fields["resumePodPhase"] = "Returning"; data.Fields["resumePodPlayer"] = true;
        var pod = new NativeObject(); pod.Fields["resumePodData"] = data;
        var crew = new Dictionary<string, int> { ["Marine"] = 2 }; pod.Fields["resumeReturnCrew"] = crew;
        var occurrence = Guid.NewGuid(); Assert.True(adapter.Observe(pod, occurrence, "ship-guid", true));
        var id = adapter.IdentityFor(data)!.Value; crew["Marine"] = 9; Assert.Equal(2, pods.Get(id)!.ReturnCrew["Marine"]);
        var saved = persistence.Provider.Capture(); adapter.Clear(); persistence.Provider.Restore(hub.CurrentSession!, saved);
        adapter.Loaded(data, id); pod.Fields["resumeReturnCrew"] = new Dictionary<string, int>();
        Assert.True(adapter.Observe(pod, occurrence, "ship-guid")); Assert.Equal(2, pods.Get(id)!.ReturnCrew["Marine"]);
        Assert.False(adapter.RestoreReturnManifest(pod, "another-ship"));
        Assert.True(adapter.RestoreReturnManifest(pod, "ship-guid"));
        var restoredCrew = Assert.IsType<Dictionary<string, int>>(pod.Fields["resumeReturnCrew"]);
        Assert.Equal(2, restoredCrew["Marine"]); restoredCrew["Marine"] = 8;
        Assert.Equal(2, pods.Get(id)!.ReturnCrew["Marine"]);
        var duplicate = new NativeObject(); adapter.Loaded(duplicate, id);
        Assert.True(adapter.Conflicted(id)); Assert.False(adapter.RestoreReturnManifest(pod, "ship-guid"));
        Assert.False(adapter.Observe(pod, occurrence, "ship-guid", true));
        persistence.Provider.Restore(hub.CurrentSession!, null);
        Assert.False(adapter.Observe(pod, occurrence, "ship-guid", true)); Assert.Empty(pods.Snapshot);
    }
}
