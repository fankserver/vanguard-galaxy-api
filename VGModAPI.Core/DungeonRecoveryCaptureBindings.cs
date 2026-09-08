namespace VGModAPI.Core;

internal static class DungeonRecoveryCaptureBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("recoveryDonorRequest", BindingCatalog.BoardingOperation, "CheckReinforcementRequest", false, "System.Void", "Source.Dungeon.DungeonSimulation"),
        new("recoveryDonorSpawn", BindingCatalog.BoardingOperation, "SpawnEnemyPods", false, "System.Void", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>", "UnityEngine.Transform"),
        new("recoveryDonorUpdate", "Source.SpaceShip.Auto.BoardingReinforcementActions", "Update", false, "System.Void", "System.Single"),
        new("recoveryResumeShip", BindingCatalog.BoardingManager, "ResumeOperation", false, BindingCatalog.BoardingOperation, BindingCatalog.Boardable, "System.Boolean"),
        new("recoveryResumeLocation", BindingCatalog.BoardingManager, "ResumeOperation", false, BindingCatalog.BoardingOperation, "Behaviour.Unit.SpaceShip", BindingCatalog.BoardingLocation, "System.Boolean"),
        new("recoveryReconstruct", BindingCatalog.BoardingManager, "ReconstructPodsFromData", false, "System.Void", BindingCatalog.Boardable, "System.Boolean"),
        new("recoveryAttach", DungeonPodResumeBindings.Pod, "Attach", false, "System.Void"),
        new("recoveryArrival", DungeonPodResumeBindings.Pod, "Arrive", false, "System.Void"),
        new("recoveryTerminal", BindingCatalog.BoardingOperation, "HandleSimulationComplete", false, "System.Void", "Source.Dungeon.DungeonSimulation"),
        new("recoverySerialization", BindingCatalog.Save, "SaveCurrentState", true, "LightJson.JsonObject"),
        new("recoveryOperationTick", BindingCatalog.BoardingOperation, "Tick", false, "System.Void", "System.Single"),
        new("recoveryStartShip", BindingCatalog.BoardingManager, "StartOperation", false, BindingCatalog.BoardingOperation, "Behaviour.Unit.SpaceShip", BindingCatalog.Boardable, BindingCatalog.BoardingOptions, "System.Boolean"),
        new("recoveryStartLocation", BindingCatalog.BoardingManager, "StartOperation", false, BindingCatalog.BoardingOperation, "Behaviour.Unit.SpaceShip", BindingCatalog.BoardingLocation, BindingCatalog.BoardingOptions, "System.Boolean")
    };
}
