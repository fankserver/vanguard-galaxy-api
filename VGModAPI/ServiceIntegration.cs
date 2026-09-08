using System;
using VGModAPI.Core;

namespace VGModAPI;

public sealed partial class Plugin
{
    private ModServices? _serviceRoot;

    private void PublishServiceRoot()
    {
        var hub = _hub!;
        var lifecycle = new LifecycleServiceView(hub);
        var mods = _modCatalog!;
        var missions = new MissionServiceView(hub, ModApi.Missions);
        var travel = new TravelServiceView(hub, ModApi.Travel);
        var station = new StationServiceView(hub, ModApi.Station);
        var root = new ModServices(lifecycle, mods, (_persistence ??= new PersistenceService(hub)), missions, travel, station);
        // Deferred cleanup preserves terminal lifecycle delivery when shutdown starts inside a callback.
        foreach (var view in new IDisposable[] { lifecycle, mods, missions, travel, station, _persistence! })
            hub.Services.AfterStopped(view.Dispose);
        ModApi.PublishServices(root);
        _serviceRoot = root;
    }
}
