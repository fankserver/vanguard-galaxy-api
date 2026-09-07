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
/// Exercises the PRODUCTION bindings and adapter — <see cref="StoryNativeBindings"/> and
/// <see cref="StoryNativeWorld"/> — against reflection doubles shaped like the installed assembly.
/// The Cecil pins say the shape is real; these tests say the code that uses that shape works: the
/// expression-built generator, the field mapping, the enum parsing, and above all that a mission this
/// API installs can actually be SAVED, which is what a missing source faction breaks.
/// </summary>
[Collection("story-native")]
public sealed class StoryNativeAdapterTests : IDisposable
{
    private static readonly StoryFactionId Trading = new("TradingGuild");
    private readonly Source.Player.GamePlayer _player = new();

    public StoryNativeAdapterTests()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = _player;
        _player.currentPointOfInterest = new Source.Galaxy.MapPointOfInterest { guid = "poi-source" };
    }

    public void Dispose()
    {
        StoryMission.allMissions.Clear();
        Source.Player.GamePlayer.current = null;
    }

    private static StoryNativeWorld World(Action<Exception>? fault = null)
        => new(new StoryNativeBindings(typeof(StoryMission).Assembly), () => { }, fault);

    private static StoryMissionDefinition Definition(string local = "salvage-run",
        StoryDifficulty difficulty = StoryDifficulty.VeryHard, IEnumerable<StoryObjective>? objectives = null)
        => new(local, "Salvage run", "Recover the drifting cargo.", Trading,
            new[] { new StoryStep("Reach the wreck", objectives ?? new[]
            {
                StoryObjective.TravelTo("poi-guid-1", 5),
                StoryObjective.CollectCredits(250)
            }, requireAllObjectives: true) },
            new[] { new StoryReward(StoryRewardKind.Credits, 500), new StoryReward(StoryRewardKind.Experience, 40) },
            difficulty, StoryRetention.Campaign, canAbandon: true, category: "story", completionText: "done",
            choiceKeys: new[] { "branch" });

    private static string Identifier(string local = "salvage-run", Guid? occurrence = null)
        => StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", local), occurrence ?? Guid.NewGuid());

    [Fact]
    public void TheBindingsResolveTheWholeStorySurfaceFromADeclaredShape()
    {
        var exception = Record.Exception(() => new StoryNativeBindings(typeof(StoryMission).Assembly));
        Assert.Null(exception);
        // A world without the story surface cannot bind at all, so nothing is half-installed.
        Assert.ThrowsAny<Exception>(() => new StoryNativeBindings(typeof(string).Assembly));
    }

    /// <summary>
    /// The mission this API installs must survive the game's own save. That is the whole reason the
    /// definition carries a source faction: the game reads its identifier unconditionally.
    /// </summary>
    [Fact]
    public void AnInstalledMissionIsBuiltThroughVanillaFactoriesAndCanBeSaved()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(StoryMission.allMissions.ContainsKey(identifier));

        var mission = StoryMission.Get(_player, identifier);
        Assert.Equal(identifier, mission.storyId);
        Assert.Equal("Salvage run", mission.name);
        Assert.Equal("story", mission.category);
        Assert.Equal("done", mission.completionText);
        Assert.True(mission.canAbandon);
        // Every vanilla generator sets these; the API sets the same ones.
        Assert.NotNull(mission.sourceFaction);
        Assert.Equal("TradingGuild", mission.sourceFaction!.identifier);
        Assert.True(mission.dynamicLevel);
        Assert.Equal("poi-source", mission.sourcePoi!.guid);
        Assert.Equal(MissionDifficulty.Skull, mission.difficulty);       // the API's fourth tier
        // Objectives and rewards come from the game's own factories with the subset's fields set.
        var step = Assert.Single(mission.steps);
        Assert.Equal("Reach the wreck", step.description);
        Assert.True(step.requireAllObjectives);
        var travel = Assert.IsType<Source.MissionSystem.Objectives.TravelToPOI>(step.objectives[0]);
        Assert.Equal("poi-guid-1", travel.targetPOI);
        Assert.Equal(5f, travel.requiredVisitTime);
        Assert.Equal(250, Assert.IsType<Source.MissionSystem.Objectives.CollectCredits>(step.objectives[1]).requiredAmount);
        var credits = Assert.IsType<Source.MissionSystem.Rewards.Credits>(mission.rewards[0]);
        Assert.Equal(500, credits.amount);
        Assert.Equal(500, credits.baseAmount);
        Assert.Equal(40, Assert.IsType<Source.MissionSystem.Rewards.Experience>(mission.rewards[1]).amount);

        // The save the game would write does not throw and carries what it needs.
        var saved = mission.SerializeLikeTheGame();
        Assert.Contains("faction:TradingGuild", saved);
        Assert.Contains("poi:poi-source", saved);
        Assert.DoesNotContain("faction:,", saved);
    }

    [Fact]
    public void EveryMappedDifficultyParsesAndAnUnknownFactionRefusesInstallation()
    {
        using var world = World();
        foreach (StoryDifficulty tier in Enum.GetValues(typeof(StoryDifficulty)))
        {
            var identifier = Identifier("tier-" + (int)tier);
            Assert.True(world.Install(identifier, Definition("tier-" + (int)tier, tier)).Applied);
            var mission = StoryMission.Get(_player, identifier);
            Assert.Equal(StoryContentPolicy.DifficultyName(tier), mission.difficulty.ToString());
        }
        Assert.True(world.KnowsFaction("TradingGuild"));
        Assert.False(world.KnowsFaction("NoSuchFaction"));
        // A definition naming an unknown faction cannot even build its mission.
        var unknown = new StoryMissionDefinition("ghost", "t", "d", new StoryFactionId("NoSuchFaction"),
            new[] { new StoryStep("s", new[] { StoryObjective.CollectCredits(1) }) });
        var identifierForUnknown = Identifier("ghost");
        Assert.True(world.Install(identifierForUnknown, unknown).Applied);        // installing is catalog-only
        Assert.ThrowsAny<Exception>(() => StoryMission.Get(_player, identifierForUnknown));
    }

    /// <summary>Installation never replaces content the world already holds, ours or anyone else's.</summary>
    [Fact]
    public void InstallationRefusesAnIdentifierTheCatalogAlreadyHoldsAndUninstallsOnlyItsOwn()
    {
        using var world = World();
        var identifier = Identifier();
        var foreign = new StoryMission("vanilla.story", _ => new Mission(), null, "hint");
        StoryMission.Add(foreign);
        Assert.Equal(StoryWorldStatus.AlreadyPresent, world.Install("vanilla.story", Definition()).Status);
        Assert.Same(foreign, StoryMission.allMissions["vanilla.story"]);
        Assert.False(world.Uninstall("vanilla.story"));
        Assert.Same(foreign, StoryMission.allMissions["vanilla.story"]);

        Assert.True(world.Install(identifier, Definition()).Applied);
        var ours = StoryMission.allMissions[identifier];
        // An entry someone else replaced is no longer ours to remove.
        StoryMission.Add(new StoryMission(identifier, _ => new Mission(), null, "hint"));
        Assert.True(world.Uninstall(identifier));
        Assert.NotSame(ours, StoryMission.allMissions[identifier]);
        Assert.True(StoryMission.allMissions.ContainsKey(identifier));
    }

    /// <summary>
    /// Acceptance is verified against the player, and the game's own duplicate-story refusal is what
    /// makes a repeated occurrence need its own identifier: the base identifier alone would be
    /// accepted exactly once per save.
    /// </summary>
    [Fact]
    public void AcceptanceIsVerifiedAndRepeatedOccurrencesNeedTheirOwnIdentifiers()
    {
        using var world = World();
        var first = Identifier();
        Assert.True(world.Install(first, Definition()).Applied);
        Assert.True(world.Accept(first).Applied);
        Assert.Equal("added:" + first, _player.AcceptanceLog.Last());
        Assert.Single(_player.missions);

        // The same identifier a second time is the game's refusal, surfaced rather than assumed.
        Assert.Equal(StoryWorldStatus.AlreadyPresent, world.Accept(first).Status);
        // An identifier this adapter did not install is never accepted.
        Assert.Equal(StoryWorldStatus.Refused, world.Accept(Identifier("other")).Status);

        // Completing it archives the base identity, and a SECOND occurrence still works because it
        // carries its own identifier.
        var mission = _player.GetActiveStoryMission(first)!;
        _player.RemoveMission(mission, completed: true);
        Assert.Contains(first, _player.missionsArchive);
        Assert.Equal(StoryWorldStatus.AlreadyPresent, world.Accept(first).Status);
        var second = Identifier();
        Assert.True(world.Install(second, Definition()).Applied);
        Assert.True(world.Accept(second).Applied);
        Assert.Equal(second, Assert.Single(_player.missions).storyId);
    }

    [Fact]
    public void ReleaseAbandonsWithoutArchivingAndRefusesToDeclareACompletion()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(world.Accept(identifier).Applied);
        // A completion is the game's to make: while it still holds the mission, the API refuses.
        Assert.Equal(StoryWorldStatus.Refused, world.Release(identifier, StoryOutcome.Completed).Status);
        Assert.Single(_player.missions);
        Assert.True(world.Release(identifier, StoryOutcome.Abandoned).Applied);
        Assert.Empty(_player.missions);
        // Abandonment must not leave the archive claiming the story was finished.
        Assert.DoesNotContain(identifier, _player.missionsArchive);
        Assert.Equal("abandoned:" + identifier, _player.AcceptanceLog.Last());
        // Nothing left to end is not a failure.
        Assert.True(world.Release(identifier, StoryOutcome.Abandoned).Applied);
    }

    [Fact]
    public void RollbackRemovesExactlyTheMissionThatAcceptanceProduced()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var accepted = Assert.Single(_player.missions);
        Assert.True(world.RollbackAccept(identifier).Applied);
        Assert.Empty(_player.missions);
        Assert.DoesNotContain(identifier, _player.missionsArchive);
        Assert.Equal("abandoned:" + identifier, _player.AcceptanceLog.Last());
        Assert.Equal("Salvage run", accepted.name);
        // An acceptance this adapter never made is never rolled back.
        Assert.Equal(StoryWorldStatus.Refused, world.RollbackAccept(Identifier("other")).Status);
    }

    [Fact]
    public void TheSnapshotReportsInstalledActiveAndArchivedIdentifiers()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        Assert.True(world.Accept(identifier).Applied);
        _player.missionsArchive.Add("vanilla.done");
        var snapshot = world.Snapshot()!;
        Assert.Contains(identifier, snapshot.Installed);
        Assert.Contains(identifier, snapshot.Active);
        Assert.Contains("vanilla.done", snapshot.Archived);

        Source.Player.GamePlayer.current = null;
        Assert.Null(world.Snapshot());
        Assert.Equal(StoryWorldStatus.Unavailable, world.Accept(identifier).Status);
        Assert.Equal(StoryWorldStatus.Unavailable, world.Release(identifier, StoryOutcome.Abandoned).Status);
    }

    [Fact]
    public void DisposalRemovesOnlyTheEntriesThisAdapterInstalled()
    {
        var identifier = Identifier();
        var world = World();
        Assert.True(world.Install(identifier, Definition()).Applied);
        StoryMission.Add(new StoryMission("vanilla.story", _ => new Mission(), null, "hint"));
        world.Dispose();
        Assert.False(StoryMission.allMissions.ContainsKey(identifier));
        Assert.True(StoryMission.allMissions.ContainsKey("vanilla.story"));
        // A disposed adapter performs nothing further and reports it.
        Assert.Equal(StoryWorldStatus.Unavailable, world.Install(identifier, Definition()).Status);
        Assert.Equal(StoryWorldStatus.Unavailable, world.Accept(identifier).Status);
    }
}
