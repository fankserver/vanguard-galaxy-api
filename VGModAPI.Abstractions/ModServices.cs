using System;
using System.Threading;

namespace VGModAPI;

/// <summary>
/// API-constructed foundational service references. Objects remain stable across session replacement;
/// their availability and domain state do not. Obtain the live root after the API plugin's Awake.
/// </summary>
public sealed class ModServices
{
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly ILifecycleService _lifecycle;
    private readonly IModInformationService _mods;
    private readonly ISaveDataService _saveData;
    private readonly IMissionService _missions;
    private readonly ITravelService _travel;
    private readonly IStationService _station;
    public ILifecycleService Lifecycle { get { CheckThread(); return _lifecycle; } }
    public IModInformationService Mods { get { CheckThread(); return _mods; } }
    public ISaveDataService SaveData { get { CheckThread(); return _saveData; } }
    public IMissionService Missions { get { CheckThread(); return _missions; } }
    public ITravelService Travel { get { CheckThread(); return _travel; } }
    public IStationService Station { get { CheckThread(); return _station; } }

    internal ModServices(ILifecycleService lifecycle, IModInformationService mods, ISaveDataService saveData,
        IMissionService missions, ITravelService travel, IStationService station)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _saveData = saveData ?? throw new ArgumentNullException(nameof(saveData));
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _travel = travel ?? throw new ArgumentNullException(nameof(travel));
        _station = station ?? throw new ArgumentNullException(nameof(station));
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI service access requires the Unity main thread.");
    }
}
