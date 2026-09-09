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
        var root = new ModServices(lifecycle, mods, (_persistence ??= new PersistenceService(hub)), missions, travel, station,
            _recipes, _recipeQuotes, _craftingJobs, _craftingCommands);
        // Deferred cleanup preserves terminal lifecycle delivery when shutdown starts inside a callback.
        foreach (var view in new IDisposable[] { mods, missions, travel, station, _persistence!, _recipes, _recipeQuotes, _craftingJobs, _craftingCommands })
            hub.Services.AfterStopped(view.Dispose);
        ModApi.PublishServices(root);
        _serviceRoot = root;
    }
}
