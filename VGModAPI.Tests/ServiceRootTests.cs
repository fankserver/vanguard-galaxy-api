using System;
using System.Collections.Generic;
using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

[CollectionDefinition("service-root", DisableParallelization = true)]
public sealed class ServiceRootCollection { }

[Collection("service-root")]
public sealed class ServiceRootTests
{
    private static void Publish(ModServices root) => typeof(ModApi).GetMethod("PublishServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { root });
    private static void Clear(ModServices root) => typeof(ModApi).GetMethod("ClearServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { root });
    private static ModServices Compose(LifecycleHub hub, ModInformationCatalog catalog)
    {
        var lifecycle = hub;
        var mods = catalog;
        var missions = new MissionTransitions(hub);
        var travel = new TravelEvents(hub);
        var station = new StationEvents(hub);
        var jobs = new CraftingJobService(hub, null, hub.ReportSubscriberFailure);
        var commands = new CraftingCommandService(hub, jobs, null, _ => { });
        var gameplayUi = new GameplayUiService(hub);
        var dungeonCombat = new BoardingCombatService(hub, hub.ReportSubscriberFailure);
        var dungeonRewards = new DungeonRewardService(hub, hub.ReportSubscriberFailure);
        var dungeonOperations = new BoardingService(hub, hub.ReportSubscriberFailure);
        var dungeonSettlement = new DungeonSettlementService(hub, dungeonOperations, hub.ReportSubscriberFailure);
        var dungeonCommands = new BoardingCommandService(hub, dungeonOperations, null, () => false);
        var dungeonTactics = new VGModAPI.Runtime.BoardingTacticalAdapter(hub, dungeonCommands);
        var dungeonPanel = new DungeonPanelService(hub, null, hub.ReportSubscriberFailure);
        var dungeons = new DungeonService(hub, null, null, null, hub.ReportSubscriberFailure);
        var dungeonFacade = new DungeonFacade(dungeons, dungeonCombat, dungeonRewards, dungeonCommands, dungeonTactics, dungeonOperations, dungeonSettlement, dungeonPanel);
        foreach (var disposable in new IDisposable[] { lifecycle, mods, missions, travel, station, gameplayUi, dungeonFacade }) hub.Services.AfterStopped(disposable.Dispose);
        return (ModServices)typeof(ModServices).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0].Invoke(
            new object[] { lifecycle, mods, new ModSettingsService(hub, (_, _) => null), new PersistenceService(hub), missions, travel, station,
                new RecipeCatalogService(hub, null, _ => { }), new RecipeQuoteService(hub, null, _ => { }), jobs, commands, new HudService(hub, hub.ReportSubscriberFailure), new ForgeUiService(hub, null, hub.ReportSubscriberFailure), new BoardingRuleService(hub, hub.ReportSubscriberFailure), dungeonFacade, new StoryMissionService(hub.Services, null, null, (_, _) => null, checkThread: hub.CheckThread), new BarService(null, hub, (_, _) => null, _ => false, hub.CheckThread),
                new WorldContentService(hub, new WorldDefinitionRegistry((_, _) => null, hub.CheckThread), null!, () => false),
                new DialogueService(hub.Services.Get("dialogue"), hub.CheckThread, _ => { }, new StoryCharacterService(hub)),
                new GameService(hub, new NavigationService(hub, _ => null, (_, _, _) => NavigationStatus.Unavailable, (_, _) => null), new InventoryService(hub, () => null), new StoryMissionService(hub.Services, null, hub, (_, _) => null), new BarService(null, hub, (_, _) => null, _ => false, hub.CheckThread)),
                new OwnedItemService(hub, (_, _) => null),
                new OwnedRecipeService(hub, (_, _) => null, _ => null, _ => { }, _ => { }),
                gameplayUi });
    }

    [Fact]
    public void PublicationIsNonNullableThreadBoundAndExactInstanceClearingPreservesNewLifetime()
    {
        Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(hub, () => Array.Empty<LoadedPluginInformation>());
        var root = Compose(hub, catalog);
        using var otherHub = new LifecycleHub((_, _) => { });
        using var otherCatalog = new ModInformationCatalog(otherHub, () => Array.Empty<LoadedPluginInformation>());
        var other = Compose(otherHub, otherCatalog);
        try
        {
            Publish(root);
            Assert.Same(root, ModApi.Services);
            Assert.True(root.Mods.Availability.IsAvailable);
            Assert.True(root.Settings.Availability.IsAvailable);
            Assert.False(root.SaveData.Availability.IsAvailable);
            Assert.False(root.Missions.Availability.IsAvailable);
            Assert.Same(root.GameplayUi, ModApi.Services.GameplayUi);
            Assert.False(root.GameplayUi.Availability.IsAvailable);
            Assert.Null(root.GameplayUi.Current);
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = root.GameplayUi));
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = ModApi.Services));
            Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => _ = root.Mods));
            Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(() => Publish(other)).InnerException);
            Clear(root); hub.Dispose();
            Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
            Assert.Equal(ServiceUnavailableReason.ApiStopped, root.Mods.Availability.Reason);
            Assert.Equal(ServiceUnavailableReason.ApiStopped, root.GameplayUi.Availability.Reason);
            Publish(other);
            Clear(root);
            Assert.Same(other, ModApi.Services);
        }
        finally { Clear(root); Clear(other); }
    }

    [Fact]
    public void DungeonServicesKeepThreadGuardAndReportStoppedAfterShutdown()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(hub, () => Array.Empty<LoadedPluginInformation>());
        var root = Compose(hub, catalog);
        Assert.IsType<TargetInvocationException>(ServiceNotificationTests.OnWorker(() => typeof(ModServices).GetProperty("Dungeons")!.GetValue(root)));
        hub.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, root.Dungeons.Availability.Reason);
    }

    [Fact]
    public void RetainedInventoryReportsStoppedAfterRootShutdown()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var catalog = new ModInformationCatalog(hub, () => Array.Empty<LoadedPluginInformation>());
        var root = Compose(hub, catalog);
        try
        {
            Publish(root);
            var inventory = ModApi.Services.Mods;
            inventory.Refresh();
            Clear(root);
            hub.Dispose();
            Assert.Throws<InvalidOperationException>(() => _ = ModApi.Services);
            Assert.Equal(ModInventoryStatus.Stopped, inventory.Refresh().Status);
        }
        finally { Clear(root); }
    }

    [Fact]
    public void DeferredViewCleanupPreservesTerminalCallbacksAndIsolatesCleanupFailures()
    {
        var failures = new List<Exception>();
        using var hub = new LifecycleHub((_, error) => failures.Add(error));
        hub.SetAvailable("session-lifecycle", "Bound.");
        ILifecycleService view = hub;
        var seen = new List<string>();
        view.Changed += fact => { if (fact.Kind == LifecycleEventKind.SessionStarting) hub.Dispose(); };
        view.Changed += fact => seen.Add(fact.Kind.ToString());
        hub.Services.AfterStopped(() => throw new Exception());
        hub.Services.AfterStopped(() => { seen.Add("cleanup"); hub.Dispose(); });
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.Equal(new[] { "SessionInvalidated", "cleanup" }, seen);
        Assert.Single(failures);
        Assert.Throws<ObjectDisposedException>(() => view.Changed += _ => { });
    }
}
