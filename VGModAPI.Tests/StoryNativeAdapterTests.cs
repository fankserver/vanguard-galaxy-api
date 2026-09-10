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
        MissionObjective.DuringCreate = null;
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
                StoryObjective.TravelTo("poi-guid-1", requireNewVisit: true),
                StoryObjective.CollectCredits(250)
            }, requireAllObjectives: true) },
            new[] { StoryReward.Credits(500), StoryReward.Experience(40) },
            difficulty, StoryRetention.Campaign, canAbandon: true, category: "story", completionText: "done",
            choiceKeys: new[] { "branch" });

    private static string Identifier(string local = "salvage-run", Guid? occurrence = null)
        => StoryContentPolicy.OccurrenceIdentifier(new StoryContentId("anima", local), occurrence ?? Guid.NewGuid());

    [Fact]
    public void ScriptedProgressTargetsCurrentPlayerAndRejectsInactiveSteps()
    {
        using var world = World();
        var identifier = Identifier();
        var definition = new StoryMissionDefinition("scripted", "Title", "Description", Trading,
            new[] {
                new StoryStep("First", new[] { StoryObjective.Scripted("first", "Talk", 2) }),
                new StoryStep("Next", new[] { StoryObjective.Scripted("next", "Report") }) });
        Assert.True(world.Install(identifier, definition).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var bindings = new StoryNativeBindings(typeof(StoryMission).Assembly);
        var oldMission = (Mission)bindings.ActiveStory(_player, identifier)!;
        var layout = new StoryObjectiveLayout(definition);
        Assert.True(layout.TryResolve("first", out var first));
        Assert.True(layout.TryResolve("next", out var next));
        Assert.False(world.SetScriptedProgress(identifier, next, 1).Applied);
        Assert.True(world.SetScriptedProgress(identifier, first, 1).Applied);
        Assert.True(world.SetScriptedProgress(identifier, first, 2).Applied);
        Assert.True(world.SetScriptedProgress(identifier, first, 2).Applied);
        var replacement = new Source.Player.GamePlayer();
        Source.Player.GamePlayer.current = replacement;
        var newMission = bindings.BuildMission(replacement, identifier);
        bindings.Accept(replacement, newMission);
        Assert.True(world.SetScriptedProgress(identifier, first, 1).Applied);
        Assert.Equal(2, ((Source.MissionSystem.Objectives.TriggerObjective)oldMission.steps[0].objectives[0]).currentAmount);
        Assert.Equal(1, ((Source.MissionSystem.Objectives.TriggerObjective)((Mission)newMission).steps[0].objectives[0]).currentAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TravelObservationRejectsChangesDuringCompletionCallback(int change)
    {
        using var world = World();
        var identifier = Identifier();
        var expected = StoryObjective.TravelTo("poi-guid-1", requireNewVisit: true).WithKey("visit");
        var definition = Definition(objectives: new[] { expected });
        Assert.True(world.Install(identifier, definition).Applied);
        Assert.True(world.Accept(identifier).Applied);
        Assert.True(new StoryObjectiveLayout(definition).TryResolve("visit", out var slot));
        var bindings = new StoryNativeBindings(typeof(StoryMission).Assembly);
        var mission = (Mission)bindings.ActiveStory(_player, identifier)!;
        var objective = (Source.MissionSystem.Objectives.TravelToPOI)mission.steps[0].objectives[0];
        Assert.Equal(0, world.ReadProgress(identifier, slot, expected, () => true));
        objective.Completion = () => true;
        Assert.Equal(1, world.ReadProgress(identifier, slot, expected, () => true));
        objective.Completion = () =>
        {
            if (change == 0) Source.Player.GamePlayer.current = new Source.Player.GamePlayer();
            if (change == 1) mission.steps[0].objectives[0] = new Source.MissionSystem.Objectives.TravelToPOI();
            if (change == 2) objective.targetPOI = "changed";
            return true;
        };
        Assert.Null(world.ReadProgress(identifier, slot, expected, () => true));
    }

    [Fact]
    public void NewVisitTravelAndReturnToSourceCaptureTheNativeVisitBaseline()
    {
        using var world = World();
        var embassy = new Source.Galaxy.MapPointOfInterest { guid = "embassy", lastVisitedTime = 41.5f };
        var wreck = new Source.Galaxy.MapPointOfInterest { guid = "poi-guid-1", lastVisitedTime = 7f };
        var galaxy = new Source.Galaxy.GalaxyMapData();
        galaxy.AddPoi(embassy); galaxy.AddPoi(wreck);
        Source.Galaxy.GalaxyMapData.current = galaxy;
        _player.currentPointOfInterest = embassy; // where the mission is built = its source
        try
        {
            var definition = Definition(objectives: new[]
            {
                StoryObjective.TravelTo("poi-guid-1", requireNewVisit: true).WithKey("visit"),
                StoryObjective.TravelTo("poi-guid-1").WithKey("ever"),
                StoryObjective.ReturnToSource().WithKey("return")
            });
            var identifier = Identifier();
            Assert.True(world.Install(identifier, definition).Applied);
            var mission = StoryMission.Get(_player, identifier);
            var step = Assert.Single(mission.steps);
            var fresh = (Source.MissionSystem.Objectives.TravelToPOI)step.objectives[0];
            Assert.Equal(7f, fresh.requiredVisitTime);   // must visit AFTER the recorded 7f
            var ever = (Source.MissionSystem.Objectives.TravelToPOI)step.objectives[1];
            Assert.Equal(0f, ever.requiredVisitTime);    // any recorded visit counts
            var back = (Source.MissionSystem.Objectives.TravelToPOI)step.objectives[2];
            Assert.Equal("embassy", back.targetPOI);     // resolved per occurrence, no authored guid
            Assert.Equal(41.5f, back.requiredVisitTime); // the player must LEAVE and come back
            // Observation resolves the return target from the mission's own source, and refuses a swap.
            Assert.True(world.Accept(identifier).Applied);
            var layout = new StoryObjectiveLayout(definition);
            Assert.True(layout.TryResolve("return", out var slot));
            Assert.Equal(0, world.ReadProgress(identifier, slot, definition.Steps[0].Objectives[2], () => true));
            embassy.lastVisitedTime = 90f;
            Assert.Equal(1, world.ReadProgress(identifier, slot, definition.Steps[0].Objectives[2], () => true));
            var held = (Mission)new StoryNativeBindings(typeof(StoryMission).Assembly).ActiveStory(_player, identifier)!;
            ((Source.MissionSystem.Objectives.TravelToPOI)held.steps[0].objectives[2]).targetPOI = "elsewhere";
            Assert.Null(world.ReadProgress(identifier, slot, definition.Steps[0].Objectives[2], () => true));
        }
        finally { Source.Galaxy.GalaxyMapData.current = null; _player.currentPointOfInterest = new Source.Galaxy.MapPointOfInterest { guid = "poi-source" }; }
    }

    [Fact]
    public void AutoCompleteIsInstalledOnTheNativeMissionOnlyWhenDeclared()
    {
        using var world = World();
        var identifier = Identifier("auto");
        Assert.True(world.Install(identifier, Definition("auto").WithAutoComplete()).Applied);
        Assert.True(StoryMission.Get(_player, identifier).autoComplete);
        var plain = Identifier("plain");
        Assert.True(world.Install(plain, Definition("plain")).Applied);
        Assert.False(StoryMission.Get(_player, plain).autoComplete);
    }

    [Fact]
    public void GatherAndKillObjectivesBuildThroughVanillaFactoriesAndObserve()
    {
        using var world = World();
        var ore = new Behaviour.Item.InventoryItemType { identifier = "OreCommon1" };
        Behaviour.Item.InventoryItemType.TestItems["OreCommon1"] = ore;
        try
        {
            var objectives = new[]
            {
                StoryObjective.SalvageItems(1, "monsoon-poi").WithKey("salvage"),
                StoryObjective.MineItems("OreCommon1", 40, "singers-field").WithKey("mine"),
                StoryObjective.KillEnemies(5, new StoryFactionId("TradingGuild")).WithKey("repel")
            };
            var identifier = Identifier();
            var definition = Definition(objectives: objectives);
            Assert.True(world.Install(identifier, definition).Applied);
            var mission = StoryMission.Get(_player, identifier);
            var step = Assert.Single(mission.steps);
            var salvage = Assert.IsType<Source.MissionSystem.Objectives.Salvage>(step.objectives[0]);
            Assert.Null(salvage.itemType); // native meaning: any material at the wreck counts
            Assert.Equal(1, salvage.requiredAmount);
            Assert.Equal("monsoon-poi", salvage.targetPOI);
            var mine = Assert.IsType<Source.MissionSystem.Objectives.Mining>(step.objectives[1]);
            Assert.Same(ore, mine.itemType);
            Assert.Equal(40, mine.requiredAmount);
            Assert.Equal("singers-field", mine.targetPOI);
            var kill = Assert.IsType<Source.MissionSystem.Objectives.KillEnemies>(step.objectives[2]);
            Assert.Equal(5, kill.requiredAmount);
            Assert.Equal("TradingGuild", kill.enemyFaction!.identifier);
            Assert.Null(kill.shipType);

            // Observation reads the native trigger-driven counts from the held mission, clamped.
            Assert.True(world.Accept(identifier).Applied);
            var layout = new StoryObjectiveLayout(definition);
            var held = (Mission)new StoryNativeBindings(typeof(StoryMission).Assembly).ActiveStory(_player, identifier)!;
            var heldMine = (Source.MissionSystem.Objectives.Mining)held.steps[0].objectives[1];
            var heldKill = (Source.MissionSystem.Objectives.KillEnemies)held.steps[0].objectives[2];
            Assert.True(layout.TryResolve("mine", out var mineSlot));
            Assert.True(layout.TryResolve("repel", out var killSlot));
            Assert.Equal(0, world.ReadProgress(identifier, mineSlot, objectives[1], () => true));
            heldMine.currentAmount = 55;
            Assert.Equal(40, world.ReadProgress(identifier, mineSlot, objectives[1], () => true));
            heldKill.currentAmount = 3;
            Assert.Equal(3, world.ReadProgress(identifier, killSlot, objectives[2], () => true));
            Assert.Null(world.ReadProgress(identifier, killSlot, StoryObjective.KillEnemies(9, new StoryFactionId("TradingGuild")).WithKey("repel"), () => true));
            // A swapped identity refuses the read outright - amount alone is not the objective's identity.
            heldKill.enemyFaction = new Source.Galaxy.Faction { identifier = "SomeoneElse" };
            Assert.Null(world.ReadProgress(identifier, killSlot, objectives[2], () => true));
            heldMine.targetPOI = "elsewhere";
            Assert.Null(world.ReadProgress(identifier, mineSlot, objectives[1], () => true));
        }
        finally { Behaviour.Item.InventoryItemType.TestItems.Clear(); }
    }

    [Fact]
    public void UnknownEnemyFactionRefusesTheMissionBuildRatherThanCreatingTheFaction()
    {
        // Registration-level refusal is covered by the service tests; this is the defensive seam:
        // even a definition that slipped past registration cannot build - and the unknown identity
        // is never created through the native Get.
        using var world = World();
        var identifier = Identifier("bad-kill");
        var definition = Definition("bad-kill", objectives: new[] { StoryObjective.KillEnemies(5, new StoryFactionId("NoSuchClan")) });
        Assert.True(world.Install(identifier, definition).Applied);
        Assert.False(world.Accept(identifier).Applied);
        Assert.False(world.KnowsFaction("NoSuchClan"));
    }

    [Fact]
    public void DeliveryObjectivesAndReputationRewardsBuildThroughVanillaFactories()
    {
        using var world = World();
        var metafiber = new Behaviour.Item.InventoryItemType { identifier = "UmbralMetafiber" };
        Behaviour.Item.InventoryItemType.TestItems["UmbralMetafiber"] = metafiber;
        var station = new Source.Galaxy.POI.SpaceStation { guid = "station-1" };
        var galaxy = new Source.Galaxy.GalaxyMapData();
        galaxy.AddPoi(station);
        galaxy.AddPoi(new Source.Galaxy.MapPointOfInterest { guid = "not-a-station" });
        Source.Galaxy.GalaxyMapData.current = galaxy;
        try
        {
            Assert.True(world.KnowsItemType("UmbralMetafiber"));
            Assert.False(world.KnowsItemType("Bogus"));
            Assert.True(world.KnowsDeliveryTarget("station-1"));
            Assert.False(world.KnowsDeliveryTarget("not-a-station")); // exists, but items cannot be turned in there
            Assert.False(world.KnowsDeliveryTarget("missing"));

            var objective = StoryObjective.DeliverItems("UmbralMetafiber", 1, "station-1").WithKey("deliver");
            var definition = Definition(objectives: new[] { (StoryObjective)objective });
            var withRewards = new StoryMissionDefinition(definition.LocalId, definition.Title, definition.Description, Trading,
                definition.Steps, new[] { StoryReward.Reputation(600), StoryReward.Reputation(50, new StoryFactionId("MiningGuild")) });
            var identifier = Identifier();
            Assert.True(world.Install(identifier, withRewards).Applied);
            var mission = StoryMission.Get(_player, identifier);
            var trade = Assert.IsType<Source.MissionSystem.Objectives.TradeOffer>(Assert.Single(Assert.Single(mission.steps).objectives));
            Assert.Same(metafiber, trade.itemType);
            Assert.Equal(1, trade.requiredAmount);
            Assert.Same(station, trade.deliverTo); // the native turn-in (consumption) target is the real station instance
            var sourceRep = Assert.IsType<Source.MissionSystem.Rewards.Reputation>(mission.rewards[0]);
            Assert.Equal(600, sourceRep.amount);
            Assert.Null(sourceRep.faction); // native OnComplete falls back to the mission's source faction
            var explicitRep = Assert.IsType<Source.MissionSystem.Rewards.Reputation>(mission.rewards[1]);
            Assert.Equal(50, explicitRep.amount);
            Assert.Equal("MiningGuild", explicitRep.faction!.identifier);

            // Observation reads the native count from the HELD mission without writing it, clamped to the requirement.
            Assert.True(world.Accept(identifier).Applied);
            var layout = new StoryObjectiveLayout(withRewards);
            Assert.True(layout.TryResolve("deliver", out var slot));
            var held = (Mission)new StoryNativeBindings(typeof(StoryMission).Assembly).ActiveStory(_player, identifier)!;
            trade = Assert.IsType<Source.MissionSystem.Objectives.TradeOffer>(held.steps[0].objectives[0]);
            Assert.Equal(0, world.ReadProgress(identifier, slot, objective, () => true));
            trade.currentAmount = 3;
            Assert.Equal(1, world.ReadProgress(identifier, slot, objective, () => true));
            Assert.Null(world.ReadProgress(identifier, slot, StoryObjective.DeliverItems("UmbralMetafiber", 2, "station-1").WithKey("deliver"), () => true));
        }
        finally
        {
            Source.Galaxy.GalaxyMapData.current = null;
            Behaviour.Item.InventoryItemType.TestItems.Clear();
        }
    }

    [Fact]
    public void NativeCreditProgressReadsCurrentResourcesWithoutWritingThem()
    {
        using var world = World();
        var identifier = Identifier();
        var objective = StoryObjective.CollectCredits(100).WithKey("credits");
        var definition = Definition(objectives: new[] { objective });
        Assert.True(world.Install(identifier, definition).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var layout = new StoryObjectiveLayout(definition);
        Assert.True(layout.TryResolve("credits", out var slot));
        _player.credits = 40;
        Assert.Equal(40, world.ReadProgress(identifier, slot, objective, () => true));
        Assert.Equal(40, _player.credits);
        _player.credits = 12;
        Assert.Equal(12, world.ReadProgress(identifier, slot, objective, () => true));
        _player.credits = 200;
        Assert.Equal(100, world.ReadProgress(identifier, slot, objective, () => true));
        Assert.Null(world.ReadProgress(identifier, slot, objective, () => false));
        Assert.Null(world.ReadProgress(identifier, slot, StoryObjective.CollectCredits(99).WithKey("credits"), () => true));
    }

    [Fact]
    public void MigrationRechecksObjectiveBudgetAfterFactoryCallbacks()
    {
        using var world = World();
        var identifier = Identifier();
        var original = Definition(objectives: new[] { StoryObjective.Scripted("talk", "Talk") });
        Assert.True(world.Install(identifier, original).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var source = new StoryObjectiveLayout(original);
        var revised = Definition(objectives: new[] { StoryObjective.Scripted("talk", "Talk"), StoryObjective.Scripted("report", "Report") }).WithRevision(2, 1);
        Assert.True(source.TryMigrate(revised, out var destination));
        var bindings = new StoryNativeBindings(typeof(StoryMission).Assembly);
        var mission = (Mission)bindings.ActiveStory(_player, identifier)!;
        var step = mission.steps[0];
        MissionObjective.DuringCreate = () =>
        {
            MissionObjective.DuringCreate = null;
            var other = new Mission();
            var crowded = new MissionStep();
            for (int index = 0; index < StoryQuarantine.MaxScannedObjectives - 1; index++)
                crowded.objectives.Add(new Source.MissionSystem.Objectives.TriggerObjective());
            other.steps.Add(crowded);
            bindings.Accept(_player, other);
        };
        Assert.False(world.MigrateScripted(identifier, revised, source, destination, () => true).Applied);
        Assert.Same(step, Assert.Single(mission.steps));
        Assert.Single(step.objectives);
    }

    [Fact]
    public void ScriptedRevisionMigrationRebuildsStepsOnTheHeldMissionPreservingPartialProgress()
    {
        using var world = World();
        var identifier = Identifier();
        StoryMissionDefinition DefinitionFor(bool reverse) => new("scripted", "Title", "Description", Trading,
            reverse ? new[] { new StoryStep("Report", new[] { StoryObjective.Scripted("report", "Report") }),
                new StoryStep("Talk", new[] { StoryObjective.Scripted("talk", "Talk", 5) }) }
                : new[] { new StoryStep("Talk", new[] { StoryObjective.Scripted("talk", "Talk", 5) }),
                new StoryStep("Report", new[] { StoryObjective.Scripted("report", "Report") }) });
        var original = DefinitionFor(false);
        Assert.True(world.Install(identifier, original).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var source = new StoryObjectiveLayout(original);
        Assert.True(source.TryResolve("talk", out var talk));
        Assert.True(world.SetScriptedProgress(identifier, talk, 2).Applied);
        source = source.WithProgress("talk", 2);
        var revised = DefinitionFor(true).WithRevision(2, 1);
        Assert.True(source.TryMigrate(revised, out var destination));
        var bindings = new StoryNativeBindings(typeof(StoryMission).Assembly);
        var held = (Mission)bindings.ActiveStory(_player, identifier)!;
        Assert.True(world.MigrateScripted(identifier, revised, source, destination, () => true).Applied);
        Assert.Same(held, bindings.ActiveStory(_player, identifier));
        Assert.Equal(2, ((Source.MissionSystem.Objectives.TriggerObjective)held.steps[1].objectives[0]).currentAmount);
        Assert.Equal(0, ((Source.MissionSystem.Objectives.TriggerObjective)held.steps[0].objectives[0]).currentAmount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ScriptedMutationRefusesChangesDuringCompletionInspection(int change)
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition(objectives: new[] { StoryObjective.Scripted("beat", "Talk", 2) })).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var bindings = new StoryNativeBindings(typeof(StoryMission).Assembly);
        var mission = (Mission)bindings.ActiveStory(_player, identifier)!;
        var objective = (Source.MissionSystem.Objectives.TriggerObjective)mission.steps[0].objectives[0];
        bool valid = true;
        mission.steps[0].DuringCompletion = () =>
        {
            mission.steps[0].DuringCompletion = null;
            if (change == 0) Source.Player.GamePlayer.current = new Source.Player.GamePlayer();
            if (change == 1) mission.steps[0].objectives[0] = new Source.MissionSystem.Objectives.TriggerObjective { requiredAmount = 2 };
            if (change == 2) bindings.Abandon(_player, mission);
            if (change == 3) StoryMission.allMissions.Remove(identifier);
            if (change == 4) valid = false;
        };
        Assert.False(world.SetScriptedProgress(identifier, new StoryObjectiveLayout.Slot("beat", 0, 0, StoryObjectiveKind.Scripted, 2), 1, () => valid).Applied);
        Assert.Equal(0, objective.currentAmount);
    }

    [Fact]
    public void ScriptedMutationRefusesNonScriptedTriggerIdentity()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition(objectives: new[] { StoryObjective.Scripted("beat", "Talk", 2) })).Applied);
        Assert.True(world.Accept(identifier).Applied);
        var mission = (Mission)new StoryNativeBindings(typeof(StoryMission).Assembly).ActiveStory(_player, identifier)!;
        var objective = (Source.MissionSystem.Objectives.TriggerObjective)mission.steps[0].objectives[0];
        objective.trigger = MissionTrigger.Travel;
        Assert.False(world.SetScriptedProgress(identifier, new StoryObjectiveLayout.Slot("beat", 0, 0, StoryObjectiveKind.Scripted, 2), 1).Applied);
        Assert.Equal(0, objective.currentAmount);
    }

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
        // New-visit baseline: no recorded visit yet, so the native timestamp floor is zero.
        Assert.Equal(0f, travel.requiredVisitTime);
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

    /// <summary>
    /// The game's own AcceptMission asks IsMissionsLimitExceeded before taking a mission;
    /// AddMissionWithLog does not, so the adapter asks the game's own method itself rather than a
    /// number it guessed. A refusal happens BEFORE anything is handed over.
    /// </summary>
    [Fact]
    public void TheGamesOwnCapacityLimitIsAskedBeforeAMissionIsHandedOver()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        // One below the limit, counting the vanilla missions the player already holds.
        for (int index = 0; index < Source.Player.GamePlayer.MissionLimit - 1; index++)
            _player.missions.Add(new Mission { sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") });
        Assert.True(world.Accept(identifier).Applied);
        Assert.Equal(Source.Player.GamePlayer.MissionLimit, _player.missions.Count);

        // At the limit the next one is refused, and the world was never asked to add it.
        var second = Identifier("side-run");
        Assert.True(world.Install(second, Definition("side-run")).Applied);
        int log = _player.AcceptanceLog.Count;
        var refused = world.Accept(second);
        Assert.Equal(StoryWorldStatus.Refused, refused.Status);
        Assert.Contains("limit of " + Source.Player.GamePlayer.MissionLimit, refused.Detail);
        Assert.Equal(log, _player.AcceptanceLog.Count);
        Assert.Equal(Source.Player.GamePlayer.MissionLimit, _player.missions.Count);
    }

    /// <summary>
    /// The guards can only protect what they can still scan, so a mission that would push the world
    /// past that bound — vanilla missions and their objectives included — is refused before it is
    /// handed over rather than accepted into a world nobody could then decide about.
    /// </summary>
    [Fact]
    public void AMissionThatWouldOutgrowTheProtectionScanIsRefusedBeforeItIsHandedOver()
    {
        using var world = World();
        var identifier = Identifier();
        Assert.True(world.Install(identifier, Definition()).Applied);
        var crowded = new Mission { sourceFaction = Source.Galaxy.Faction.Get("TradingGuild") };
        var step = new MissionStep();
        for (int index = 0; index < StoryQuarantine.MaxScannedObjectives; index++)
            step.objectives.Add(new Source.MissionSystem.Objectives.CollectCredits());
        crowded.steps.Add(step);
        _player.missions.Add(crowded);
        Source.Player.GamePlayer.MissionLimit = 1000;                 // isolate the scan bound
        try
        {
            int log = _player.AcceptanceLog.Count;
            var refused = world.Accept(identifier);
            Assert.Equal(StoryWorldStatus.Refused, refused.Status);
            Assert.Contains("objectives than the protection guard can scan", refused.Detail);
            Assert.Equal(log, _player.AcceptanceLog.Count);
            Assert.DoesNotContain(_player.missions, mission => mission.storyId == identifier);

            // With room again the same acceptance succeeds, so the refusal was the bound and not the shape.
            step.objectives.Clear();
            Assert.True(world.Accept(identifier).Applied);
        }
        finally { Source.Player.GamePlayer.MissionLimit = 20; }
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
