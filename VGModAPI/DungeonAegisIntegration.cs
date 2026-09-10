using System;
using System.Reflection;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using VGModAPI.Runtime;

namespace VGModAPI;

public sealed partial class Plugin
{
    private DungeonAegisRuntime? _dungeonAegisRuntime;

    private void InstallDungeonAegis(Assembly assembly)
    {
        try
        {
            DungeonAegisBindings.Validate(assembly);
            var partType = assembly.GetType("Behaviour.Unit.CombatStationPart", true)!;
            _dungeonAegisRuntime = new DungeonAegisRuntime(assembly, _hub!, _hub!.Installations.Aegis,
                () => UnityEngine.Object.FindObjectsByType(partType),
                error => Logger.LogError(error));
            _hub.Installations.Aegis.SetAvailable(true);
        }
        catch (Exception error) { TeardownDungeonAegis(); Logger.LogError(error); }
    }

    private void TeardownDungeonAegis()
    {
        _dungeonAegisRuntime = null;
        _hub?.Installations.Aegis.SetAvailable(false);
    }
}
