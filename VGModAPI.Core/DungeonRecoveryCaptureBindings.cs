namespace VGModAPI.Core;

internal static class DungeonRecoveryCaptureBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("recoveryCancelPods", BindingCatalog.BoardingOperation, "CancelPodApproach", false, "System.Void"),
        new("recoveryCancelMovement", BindingCatalog.BoardingOperation, "CancelApproachDueToMovement", false, "System.Void"),
        new("recoveryRefundRecall", BindingCatalog.BoardingOperation, "RecallAllPods", false, "System.Void"),
        new("recoveryRefundDocked", BindingCatalog.BoardingOperation, "ReturnDockedPodCrew", false, "System.Void"),
        new("recoveryRefundTerminal", BindingCatalog.BoardingOperation, "TriggerPodReturn", false, "System.Void", "Source.Dungeon.DungeonSimulation"),
        new("recoveryRetired", BindingCatalog.BoardingOperation, "MarkComplete", false, "System.Void"),
        new("recoveryPendingExtraction", BindingCatalog.BoardingManager, "RecoverPendingExtraction", false, "System.Void", "Behaviour.Unit.SpaceShip", BindingCatalog.BoardingLocation),
        new("recoveryWalkComplete", BindingCatalog.BoardingOperation, "CompleteExtraction", false, "System.Void"),
        new("recoveryWalkDispatch", BindingCatalog.BoardingOperation, "HandleDockedCrewDispatch", false, "System.Void"),
        new("recoveryWalkEntry", BindingCatalog.BoardingOperation, "BeginWalkSimulation", false, "System.Void"),
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
