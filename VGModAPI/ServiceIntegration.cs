using System;
using VGModAPI.Core;

namespace VGModAPI;

public sealed partial class Plugin
{
    private ModServices? _serviceRoot;

    private void PublishServiceRoot()
    {
        var hub = _hub!;
        // Fallbacks are created only during bootstrap, never after a module stops or a session starts.
        if (_serviceRoot != null || hub.CurrentSession != null)
            throw new InvalidOperationException("Services can only be published once before a session starts.");
        var lifecycle = hub;
        var mods = _modCatalog!;
        var missions = _missions?.Events ?? new MissionTransitions(hub);
        var travel = _travel?.Events ?? new TravelEvents(hub);
        var station = _travel?.Station ?? new StationEvents(hub);
        _recipes ??= new RecipeCatalogService(hub, null, error => Logger.LogError(error));
        _recipeQuotes ??= new RecipeQuoteService(hub, null, error => Logger.LogError(error));
        _craftingJobs ??= new CraftingJobService(hub, null, hub.ReportSubscriberFailure);
        _craftingCommands ??= new CraftingCommandService(hub, _craftingJobs, null, error => Logger.LogError(error));
        _hudService ??= new HudService(hub, hub.ReportSubscriberFailure);
        _gameplayUi ??= new GameplayUiService(hub);
        _forgeUi ??= new ForgeUiService(hub, null, hub.ReportSubscriberFailure);
        _boardingRuleService ??= new BoardingRuleService(hub, hub.ReportSubscriberFailure);
        _boardingCombat ??= new BoardingCombatService(hub, hub.ReportSubscriberFailure);
        _dungeonRewards ??= new DungeonRewardService(hub, hub.ReportSubscriberFailure);
        _boardingService ??= new BoardingService(hub, hub.ReportSubscriberFailure);
        _dungeonSettlement ??= new DungeonSettlementService(hub, _boardingService, hub.ReportSubscriberFailure);
        _boardingCommands ??= new BoardingCommandService(hub, _boardingService, null,
            () => _boardingRuleService.IsEvaluating || _boardingCombat.IsEvaluating || _dungeonRewards.IsEvaluating);
        _boardingTactics ??= new Runtime.BoardingTacticalAdapter(hub, _boardingCommands);
        _dungeonPanelService ??= new DungeonPanelService(hub, null, hub.ReportSubscriberFailure);
        _dungeons ??= new DungeonContentService(hub, null, null, null, hub.ReportSubscriberFailure);
        _story ??= new StoryContentService(hub.Services, null, hub, StoryHostAuthentication.Resolve, checkThread: hub.CheckThread);
        _bars ??= new BarContentService(null, hub, StoryHostAuthentication.Resolve, _ => false, hub.CheckThread);
        _worldDefinitions ??= new WorldDefinitionRegistry((_, _) => null, hub.CheckThread);
        var ambient = _ambientTraffic ??= new AmbientTrafficService(hub);
        var protection = _unitProtection ??= new UnitProtectionService(hub);
        _worldContent ??= new WorldContentService(hub, _worldDefinitions, null!, () => false, null, ambient, protection);
        _ownedItems ??= CreateOwnedItems();
        _ownedRecipes ??= CreateOwnedRecipes();
        _inventoryService ??= CreateInventories();
        _navigationService ??= CreateNavigation();
        _dialogueService ??= new DialogueService(hub.Services.Get("dialogue"), hub.CheckThread, error => hub.ReportSubscriberFailure("dialogue", error));
        var root = new ModServices(lifecycle, mods, (_persistence ??= new PersistenceService(hub)), missions, travel, station,
            _recipes, _recipeQuotes, _craftingJobs, _craftingCommands, _hudService, _forgeUi, _boardingRuleService, _boardingCombat, _dungeonRewards, _boardingCommands, _boardingTactics, _boardingService, _dungeonSettlement, _dungeonPanelService, _dungeons, _story, _bars, _worldContent, _dialogueService, _navigationService, _ownedItems, _ownedRecipes, _inventoryService, _gameplayUi);
        // Deferred cleanup preserves terminal lifecycle delivery when shutdown starts inside a callback.
        // Content owners release their registrations before the save-data coordinator stops.
        foreach (var service in new IDisposable[] { mods, missions, travel, station, _recipes, _recipeQuotes, _craftingJobs, _craftingCommands, _hudService, _forgeUi, _boardingRuleService, _boardingCombat, _dungeonRewards, _boardingCommands, _boardingTactics, _boardingService, _dungeonSettlement, _dungeonPanelService, _dungeons, _story, _bars })
            hub.Services.AfterStopped(service.Dispose);
        hub.Services.AfterStopped(_gameplayUi.Dispose);
        hub.Services.AfterStopped(ambient.Dispose);
        hub.Services.AfterStopped(protection.Dispose);
        hub.Services.AfterStopped(StopDialogue);
        hub.Services.AfterStopped(StopNavigation);
        hub.Services.AfterStopped(StopInventories);
        hub.Services.AfterStopped(StopOwnedRecipes);
        hub.Services.AfterStopped(StopOwnedItems);
        Core.Integration.WorldShutdownRegistration.Register(hub.Services, StopWorldProtection, _persistence!);
        ModApi.PublishServices(root);
        _serviceRoot = root;
    }
}
