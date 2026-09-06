using System;
using System.Collections.Generic;
using System.Linq;
using Source.Player;
using Source.MissionSystem;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Collection("game-double")]
public sealed class MissionAdapterTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly MissionAdapter _adapter;
    private readonly GamePlayer _player = new();
    private readonly List<MissionTransition> _events = new();
    public MissionAdapterTests()
    {
        GamePlayer.current = _player;
        _adapter = new MissionAdapter(_hub, new MissionBindings(typeof(GamePlayer).Assembly), _ => { });
        _adapter.Events.Subscribe("test", _events.Add);
        var id = _hub.Begin(SessionOrigin.SaveLoad, "test.save"); _hub.PlayerReady(id); _hub.GameplayInitialized(id);
    }
    public void Dispose() { _adapter.Dispose(); _hub.Dispose(); GamePlayer.current = null; }

    [Fact]
    public void NativeAccessIsExactSnapshotAndDispatchScoped()
    {
        var mission = new Mission(); bool resolved = false, rejected = false; object? native = null;
        _adapter.Events.Subscribe("native", e =>
        {
            resolved = _adapter.Events.TryGetNative(e.Mission, out native);
            var copy = new MissionSnapshot(e.Mission.SessionId, e.Mission.InstanceId, e.Mission.DefinitionId, e.Mission.Name, e.Mission.ObjectiveTags, e.Mission.AcceptanceObserved);
            rejected = !_adapter.Events.TryGetNative(copy, out _);
        });
        Accept(mission);
        Assert.True(resolved); Assert.True(rejected); Assert.Same(mission, native);
        Assert.False(_adapter.Events.TryGetNative(_events[0].Mission, out _));
    }
    [Fact]
    public void OldFinalizerCannotEmitIntoReplacementSession()
    {
        var mission = new Mission(); var call = _adapter.Begin("accept", _player, mission)!;
        _player.missions.Add(mission);
        var id = _hub.Begin(SessionOrigin.SaveLoad, "replacement.save"); _hub.PlayerReady(id);
        _adapter.End(call);
        Assert.Equal(MissionTransitionKind.Restored, Assert.Single(_events).Kind);
    }
    [Fact]
    public void ObserverFaultStopsObservationWithoutThrowingIntoCaller()
    {
        _adapter.Guard(() => throw new InvalidOperationException("injected")); _adapter.Poll();
        Assert.Null(_adapter.Begin("accept", _player, new Mission()));
        Assert.False(_hub.Capabilities.Single(c => c.Name == "mission-transitions").Available);
    }
    [Fact]
    public void NoOpAcceptanceAndIneligibleClaimDoNotEmitCompletion()
    {
        var mission = new Mission();
        var skipped = _adapter.Begin("accept", _player, mission)!; _adapter.End(skipped);
        Assert.Empty(_events);
        Accept(mission);
        var claim = _adapter.Begin("claim", null, mission)!; _adapter.End(claim);
        Assert.Single(_events); Assert.Equal(MissionTransitionKind.Accepted, _events[0].Kind);
    }
    private void Accept(Mission mission)
    {
        var call = _adapter.Begin("accept", _player, mission)!;
        _player.missions.Add(mission); _adapter.End(call);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewardRemovalAndNestedArchiveAreVerified(bool force)
    {
        var mission = new Mission { storyId = "repeat", Claimable = !force }; Accept(mission);
        var claim = _adapter.Begin("claim", null, mission, force)!;
        var removal = _adapter.Begin("remove", _player, mission, true)!;
        _player.missions.Remove(mission);
        var archive = _adapter.Begin("archive", _player, null, definition: "repeat")!;
        _player.missionsArchive.Add("repeat"); _adapter.End(archive); _adapter.End(removal); _adapter.End(claim);
        Assert.Equal(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Completed, MissionTransitionKind.Archived }, _events.Select(e => e.Kind));
    }
    [Fact]
    public void TransientInsertionFailureAndReplacementKeepCausalOrder()
    {
        var old = new Mission(); var acceptance = _adapter.Begin("accept", _player, old)!; _player.missions.Add(old);
        var failure = _adapter.Begin("fail", null, old)!; old.failed = true;
        var removal = _adapter.Begin("remove", _player, old)!; _player.missions.Remove(old); _adapter.End(removal);
        Accept(new Mission()); _adapter.End(failure); _adapter.End(acceptance);
        Assert.Equal(new[] { MissionTransitionKind.Accepted, MissionTransitionKind.Failed, MissionTransitionKind.Removed, MissionTransitionKind.Accepted }, _events.Select(e => e.Kind));
        Assert.Equal(_events[0].Mission.InstanceId, _events[1].Mission.InstanceId);
    }
    [Fact]
    public void RestoredSpecialSlotsAndGatherTagsAreObservedWithoutAcceptance()
    {
        var mission = new Mission(); var step = new MissionStep();
        step.objectives.Add(new Source.MissionSystem.Objectives.Salvage { itemCategory = Source.MissionSystem.Objectives.ItemCategory.Salvage }); mission.steps.Add(step);
        _player.currentIndustry = mission;
        var id = _hub.Begin(SessionOrigin.SaveLoad, "rollback.save"); _hub.PlayerReady(id);
        var restored = Assert.Single(_events);
        Assert.Equal(MissionTransitionKind.Restored, restored.Kind); Assert.False(restored.Mission.AcceptanceObserved);
        Assert.Contains("item-category:Salvage", restored.Mission.ObjectiveTags);
        Assert.Contains("type:Source.MissionSystem.Objectives.Salvage", restored.Mission.ObjectiveTags);
    }
}
