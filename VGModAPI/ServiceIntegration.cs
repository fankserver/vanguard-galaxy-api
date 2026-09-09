using System;
using VGModAPI.Core;

namespace VGModAPI;

public sealed partial class Plugin
{
    private ModServices? _serviceRoot;

    private void PublishServiceRoot()
    {
        var hub = _hub!;
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
        var root = new ModServices(lifecycle, mods, (_persistence ??= new PersistenceService(hub)), missions, travel, station,
            _recipes, _recipeQuotes, _craftingJobs, _craftingCommands, _hudService, _forgeUi, _boardingRuleService, _boardingCombat, _dungeonRewards, _boardingCommands, _boardingTactics, _boardingService, _dungeonSettlement, _dungeonPanelService);
        // Deferred cleanup preserves terminal lifecycle delivery when shutdown starts inside a callback.
        foreach (var view in new IDisposable[] { mods, missions, travel, station, _persistence!, _recipes, _recipeQuotes, _craftingJobs, _craftingCommands, _hudService, _forgeUi, _boardingRuleService, _boardingCombat, _dungeonRewards, _boardingCommands, _boardingTactics, _boardingService, _dungeonSettlement, _dungeonPanelService })
            hub.Services.AfterStopped(view.Dispose);
        ModApi.PublishServices(root);
        _serviceRoot = root;
    }
}
