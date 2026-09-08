namespace VGModAPI.Core;

internal static class DungeonCrewResumeBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("crewResumeSave", "Source.CompartmentSystem.SimCrewUnit", "ToJson", false, "LightJson.JsonValue"),
        new("crewResumeLoad", "Source.CompartmentSystem.SimCrewUnit", "FromJson", true, "Source.CompartmentSystem.SimCrewUnit", "LightJson.JsonValue"),
        new("crewSimulationLoad", "Source.Dungeon.DungeonSimulation", "FromJson", true, "Source.Dungeon.DungeonSimulation", "LightJson.JsonValue"),
        new("crewSimulationSave", "Source.Dungeon.DungeonSimulation", "ToJson", false, "LightJson.JsonValue"),
        new("crewSimulationTick", "Source.Dungeon.DungeonSimulation", "Tick", false, "System.Void", "System.Single")
    };
}
