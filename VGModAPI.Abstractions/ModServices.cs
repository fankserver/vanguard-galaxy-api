using System;

namespace VGModAPI;

/// <summary>
/// API-constructed foundational service references. Objects remain stable across session replacement;
/// their availability and domain state do not. This composition contract does not bootstrap the plugin.
/// </summary>
public sealed class ModServices
{
    public ILifecycleService Lifecycle { get; }
    public IModInformationService Mods { get; }
    public ISaveDataService SaveData { get; }
    public IMissionService Missions { get; }
    public ITravelService Travel { get; }
    public IStationService Station { get; }

    internal ModServices(ILifecycleService lifecycle, IModInformationService mods, ISaveDataService saveData,
        IMissionService missions, ITravelService travel, IStationService station)
    {
        Lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        Mods = mods ?? throw new ArgumentNullException(nameof(mods));
        SaveData = saveData ?? throw new ArgumentNullException(nameof(saveData));
        Missions = missions ?? throw new ArgumentNullException(nameof(missions));
        Travel = travel ?? throw new ArgumentNullException(nameof(travel));
        Station = station ?? throw new ArgumentNullException(nameof(station));
    }
}
