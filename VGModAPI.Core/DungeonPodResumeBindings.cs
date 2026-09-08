namespace VGModAPI.Core;

internal static class DungeonPodResumeBindings
{
    internal const string Pod = "Behaviour.Persistables.BoardingPod";
    internal const string Data = "Source.Data.Persistable.BoardingPodData";
    internal static readonly MethodBinding[] Methods =
    {
        new("podReturnStart", Pod, "StartReturning", false, "System.Void", "UnityEngine.Transform", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>"),
        new("podSave", Data, "DataToJson", false, "System.Void", "LightJson.JsonObject"),
        new("podLoad", Data, "LoadFromJson", false, "System.Void", "LightJson.JsonObject"),
        new("podConfigure", BindingCatalog.BoardingManager, "ConfigureReconstructedPod", true, Pod, Pod, Data, "UnityEngine.Transform", "UnityEngine.Transform"),
        new("podReconstruct", BindingCatalog.BoardingManager, "ReconstructSinglePod", false, Pod, Data, "UnityEngine.Transform", "UnityEngine.Transform", "System.Int32&"),
        new("podRegister", BindingCatalog.BoardingOperation, "RegisterReconstructedPod", false, "System.Void", Pod),
        new("podReturned", BindingCatalog.BoardingOperation, "HandlePodCrewReturned", false, "System.Void", Pod)
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("pendingDirectives", "Source.Dungeon.DungeonSimulation", "pendingDirectives", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCrewDirective>"),
        ("directiveTarget", "Source.CompartmentSystem.SimCrewDirective", "targetCompartmentIndex", "System.Int32"),
        ("directivePriority", "Source.CompartmentSystem.SimCrewDirective", "priority", "Source.CompartmentSystem.DirectivePriority"),
        ("directiveCrew", "Source.CompartmentSystem.SimCrewDirective", "requiredCrewTypeId", "System.String"),
        ("directiveFilter", "Source.CompartmentSystem.SimCrewDirective", "unitFilter", "Source.CompartmentSystem.MovementOrderFilter"),
        ("directiveClaimed", "Source.CompartmentSystem.SimCrewDirective", "isClaimed", "System.Boolean"),
        ("directiveUnit", "Source.CompartmentSystem.SimCrewDirective", "claimingUnit", "Source.CompartmentSystem.SimCrewUnit"),
        ("resumeDirectiveTarget", "Source.CompartmentSystem.SimCrewUnit", "assignedDirectiveTarget", "System.Int32"),
        ("resumeFleeDelay", "Source.CompartmentSystem.SimCrewUnit", "fleeDelayTimer", "System.Single"),
        ("resumeWithdrawing", "Source.CompartmentSystem.SimCrewUnit", "isWithdrawing", "System.Boolean"),
        ("resumeRetreatOrigin", "Source.CompartmentSystem.SimCrewUnit", "retreatOriginIndex", "System.Int32"),
        ("resumeRecoveryProgress", "Source.CompartmentSystem.SimCrewUnit", "hpRecoveryProgress", "System.Single"),
        ("resumeDazedTime", "Source.CompartmentSystem.SimCrewUnit", "dazedTimer", "System.Single"),
        ("resumePodData", Pod, "data", Data),
        ("resumeReturnCrew", Pod, "_returnCrew", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>"),
        ("resumePodPhase", Data, "state", "Source.Data.Persistable.BoardingPodState"),
        ("resumePodCrew", Data, "crew", "System.Collections.Generic.Dictionary`2<System.String,System.Int32>"),
        ("resumePodPlayer", Data, "isPlayerOwned", "System.Boolean"),
        ("resumePodId", Data, "podId", "System.String"),
        ("resumeParentId", Data, "parentShipId", "System.String"),
        ("resumePosition", "Source.Data.Persistable.PersistableData", "position", "UnityEngine.Vector2"),
        ("resumeAngle", "Source.Data.Persistable.PersistableData", "angle", "System.Single"),
        ("resumeHullOffset", Data, "attachedHullOffset", "UnityEngine.Vector2"),
        ("resumeTargetPosition", Data, "lastKnownTargetPosition", "UnityEngine.Vector2"),
        ("resumeAttachmentOffset", Data, "targetAttachmentLocalOffset", "UnityEngine.Vector2")
    };
}
