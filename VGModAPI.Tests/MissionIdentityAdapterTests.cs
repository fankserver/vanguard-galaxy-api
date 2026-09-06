using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LightJson;
using Source.MissionSystem;
using Source.Player;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;
[Collection("game-double")]
public sealed class MissionIdentityAdapterTests
{
    private static JsonObject Root(GamePlayer player)
    {
        var data = new JsonObject(); data["missions"] = new JsonValue(player.missions.Select(m => m.ToJson()).ToList());
        data["currentBounty"] = player.currentBounty?.ToJson() ?? new JsonValue(null);
        data["currentPatrol"] = player.currentPatrol?.ToJson() ?? new JsonValue(null);
        data["currentIndustry"] = player.currentIndustry?.ToJson() ?? new JsonValue(null);
        var root = new JsonObject(); root["Player"] = new JsonValue(data); return root;
    }
    [Fact]
    public void RealServiceRestoresBeforeAdapterSeedsAllContainers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vg-identity-glue-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var hub = new LifecycleHub((_, error) => throw error);
            using var service = new PersistenceService(hub, new GenerationStore(directory), s => s, _ => new string('a', 64));
            using var adapter = new MissionAdapter(hub, new MissionBindings(typeof(GamePlayer).Assembly), error => throw error);
            adapter.EnableIdentity(service, new MissionJsonBindings(typeof(GamePlayer).Assembly));
            var player = new GamePlayer(); player.missions.Add(new Mission { name = "regular" });
            player.currentBounty = new Mission { name = "bounty" }; player.currentPatrol = new Mission { name = "patrol" }; player.currentIndustry = new Mission { name = "industry" };
            GamePlayer.current = player;
            var received = new List<MissionTransition>(); adapter.Events.Subscribe("test", received.Add);
            var session = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(session); hub.GameplayInitialized(session);
            var ids = received.ToDictionary(e => e.Mission.Name, e => e.Mission.InstanceId);
            var capture = adapter.BeginSerialization()!; var root = Root(player); adapter.EndSerialization(capture, root);
            using (adapter.BeginIdentityStore(root))
            {
                var op = Guid.NewGuid(); hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, hub.CurrentSession, op, "slot"));
                hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSucceeded, hub.CurrentSession, op, "slot"));
            }
            var replacement = new GamePlayer(); replacement.missions.Add(new Mission { name = "regular" });
            replacement.currentBounty = new Mission { name = "bounty" }; replacement.currentPatrol = new Mission { name = "patrol" }; replacement.currentIndustry = new Mission { name = "industry" };
            GamePlayer.current = replacement; received.Clear();
            session = hub.Begin(SessionOrigin.SaveLoad, "slot"); hub.PlayerReady(session);
            Assert.Equal(4, received.Count);
            Assert.All(received, e => { Assert.Equal(ids[e.Mission.Name], e.Mission.InstanceId); Assert.Equal(MissionIdentityEvidence.SavedSnapshotMatch, e.Mission.IdentityEvidence); Assert.False(e.Mission.AcceptanceObserved); });
        }
        finally { GamePlayer.current = null; if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void ReflectionReaderRejectsInvalidShapeAndDistinguishesContainers()
    {
        var json = new MissionJsonBindings(typeof(GamePlayer).Assembly); var mission = new Mission();
        Assert.NotEqual(json.CurrentFingerprint("missions", mission), json.CurrentFingerprint("currentBounty", mission));
        Assert.Throws<InvalidDataException>(() => json.SavedFingerprints(new JsonObject()));
        var root = new JsonObject(); var player = new JsonObject(); root["Player"] = new JsonValue(player);
        player["missions"] = new JsonValue(new List<JsonValue> { new JsonValue(null) });
        Assert.Throws<InvalidDataException>(() => json.SavedFingerprints(root));
    }
}
