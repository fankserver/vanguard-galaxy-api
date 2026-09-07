using System;
using System.Collections.Generic;
using System.Linq;
using Source.MissionSystem;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// The quarantine boundary, exercised on the production guard against reflection doubles shaped like
/// the installed assembly. A save keeps accepted missions as full objects, so an API-owned mission
/// comes back whether or not the module that owns it is present. These tests pin what happens then:
/// it does not advance, it does not pay out, it is not retried — and it is not touched either, so the
/// player's save keeps exactly what it had.
/// </summary>
[Collection("story-native")]
public sealed class StoryProtectionTests : IDisposable
{
    private static readonly StoryFactionId Trading = new("TradingGuild");
    private readonly Source.Player.GamePlayer _player = new();
    private readonly StoryProtection _protection = new();
    private readonly StoryQuarantine _quarantine;

    public StoryProtectionTests()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = _player;
        _quarantine = new StoryQuarantine(new StoryProtectionGuard(typeof(StoryMission).Assembly), _protection,
            () => _player.missions.Cast<object>().ToArray());
    }

    public void Dispose()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = null;
    }

    private static StoryMissionDefinition Definition() => new("salvage-run", "Salvage run", "Recover it.", Trading,
        new[] { new StoryStep("Reach the wreck", new[] { StoryObjective.TravelTo("poi-guid-1", 5) }) },
        new[] { new StoryReward(StoryRewardKind.Credits, 500) }, StoryDifficulty.Normal, StoryRetention.Campaign);

    private (string Identifier, Mission Mission) Hold(string local = "salvage-run")
    {
        var identifier = StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", local), Guid.NewGuid());
        var world = new StoryNativeWorld(new StoryNativeBindings(typeof(StoryMission).Assembly), () => { });
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(world.Accept(identifier).Applied);
        return (identifier, _player.missions.Single(mission => mission.storyId == identifier));
    }

    /// <summary>With nobody vouching for it, an owned mission is quarantined; other content never is.</summary>
    [Fact]
    public void OwnedMissionsAreQuarantinedByDefaultAndNothingElseIsEverTouched()
    {
        var owned = Hold();
        var vanilla = new Mission { storyId = "tutorial_11", sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        var unnamed = new Mission { sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        _player.missions.Add(vanilla);
        _player.missions.Add(unnamed);

        Assert.True(_quarantine.Blocks(owned.Mission));
        // Vanilla story content and missions with no identifier are not ours to judge.
        Assert.False(_quarantine.Blocks(vanilla));
        Assert.False(_quarantine.Blocks(unnamed));
        Assert.False(_quarantine.Blocks(null));
        Assert.Contains(owned.Identifier, _protection.Quarantined);
        Assert.DoesNotContain("tutorial_11", _protection.Quarantined);
    }

    /// <summary>
    /// Only the exact identifiers the owning module vouches for, in the session it vouched for them,
    /// are allowed through; withdrawing admission puts the mission straight back under quarantine.
    /// </summary>
    [Fact]
    public void AdmissionIsExactAndWithdrawnAdmissionQuarantinesAgain()
    {
        var owned = Hold();
        var other = Hold("side-run");
        _protection.Admit(Guid.NewGuid(), new[] { owned.Identifier }, "admitted");
        Assert.False(_quarantine.Blocks(owned.Mission));
        Assert.True(_quarantine.Blocks(other.Mission));

        _protection.WithdrawAll("the owning module suspended itself");
        Assert.True(_quarantine.Blocks(owned.Mission));
        Assert.Equal("the owning module suspended itself", _protection.State);
        // A session with no identity admits nothing, whatever it is handed.
        _protection.Admit(Guid.Empty, new[] { owned.Identifier }, "no session");
        Assert.True(_quarantine.Blocks(owned.Mission));
    }

    /// <summary>
    /// Objectives are recognised by identity, so a trigger dispatched to every held mission's
    /// objectives advances the ones that may run and not the ones that may not.
    /// </summary>
    [Fact]
    public void ObjectivesOfQuarantinedMissionsAreRecognisedAndOthersAreLeftAlone()
    {
        var quarantined = Hold();
        var admitted = Hold("side-run");
        _protection.Admit(Guid.NewGuid(), new[] { admitted.Identifier }, "admitted");
        var vanilla = new Source.MissionSystem.Objectives.CollectCredits();

        foreach (var objective in quarantined.Mission.steps.SelectMany(step => step.objectives))
            Assert.True(_quarantine.BlocksObjective(objective));
        foreach (var objective in admitted.Mission.steps.SelectMany(step => step.objectives))
            Assert.False(_quarantine.BlocksObjective(objective));
        Assert.False(_quarantine.BlocksObjective(vanilla));
        Assert.False(_quarantine.BlocksObjective(null));

        // Admission changes are picked up: the same objects flip when the module vouches for them.
        _protection.Admit(Guid.NewGuid(), new[] { quarantined.Identifier, admitted.Identifier }, "admitted");
        foreach (var objective in quarantined.Mission.steps.SelectMany(step => step.objectives))
            Assert.False(_quarantine.BlocksObjective(objective));
    }

    /// <summary>
    /// Quarantine changes nothing about the mission: the object stays in the player's list and
    /// serializes byte-for-byte as it was loaded, so a provider that returns later finds it intact.
    /// </summary>
    [Fact]
    public void QuarantineLeavesTheMissionAndWhatItWouldSaveExactlyAsTheyWere()
    {
        var owned = Hold();
        var before = owned.Mission.SerializeLikeTheGame();
        var missionCount = _player.missions.Count;
        var archived = _player.missionsArchive.ToArray();

        for (int index = 0; index < 3; index++)
        {
            Assert.True(_quarantine.Blocks(owned.Mission));
            foreach (var objective in owned.Mission.steps.SelectMany(step => step.objectives))
                Assert.True(_quarantine.BlocksObjective(objective));
        }

        Assert.Equal(before, owned.Mission.SerializeLikeTheGame());
        Assert.Equal(missionCount, _player.missions.Count);
        Assert.Equal(archived, _player.missionsArchive);
        Assert.Contains(owned.Mission, _player.missions);
        Assert.False(owned.Mission.failed);
    }

    /// <summary>
    /// The guard binds only against the shape it actually guards, and refuses to bind when an
    /// installable objective kind would slip past the one trigger method it protects.
    /// </summary>
    [Fact]
    public void TheGuardBindsAgainstTheDeclaredShapeAndCoversEveryInstallableObjective()
    {
        var guard = new StoryProtectionGuard(typeof(StoryMission).Assembly);
        var owned = Hold();
        Assert.Equal(owned.Identifier, guard.StoryId(owned.Mission));
        Assert.True(guard.IsMission(owned.Mission));
        Assert.False(guard.IsMission("not a mission"));
        Assert.Single(guard.Objectives(owned.Mission));
        Assert.ThrowsAny<Exception>(() => new StoryProtectionGuard(typeof(string).Assembly));
        // Every kind the API can install uses the base trigger the guard patches; a kind that
        // overrode it would refuse to bind rather than be guarded incompletely.
        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
        {
            if (StoryContentPolicy.RefuseObjective(kind) != null) continue;
            var type = typeof(StoryMission).Assembly.GetType(
                StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind))!;
            Assert.Null(type.GetMethod("ProcessMissionTrigger",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
        }
    }
}
