using System;

namespace VGModAPI.Core;

internal sealed class MethodBinding
{
    internal readonly string Key, Type, Name, ReturnType;
    internal readonly bool Static;
    internal readonly string[] Parameters;
    internal MethodBinding(string key, string type, string name, bool isStatic, string returnType, params string[] parameters)
    { Key = key; Type = type; Name = name; Static = isStatic; ReturnType = returnType; Parameters = parameters; }
}

internal static class BindingCatalog
{
    internal const string InspectedSha256 = "a2aad60bc68c31baccd636587d3c5ba4e651eacda59b0af42cd4f17f864284fb";
    internal const string Save = "Source.Util.SaveGame";
    internal const string File = "Source.Util.SaveGameFile";
    internal const string Player = "Source.Player.GamePlayer";
    internal const string Scenes = "Behaviour.Bootstrap.SceneLoader";
    // Install tiny callees before callers: patching a caller can JIT it and inline
    // an as-yet-unpatched iterator factory, permanently bypassing that factory's hook.
    internal static readonly MethodBinding[] Session =
    {
        new("loadRoutine", File, "LoadSaveGameStaged", false, "System.Collections.IEnumerator"),
        new("loadFailure", File, "HandleLoadFailure", false, "System.Void"),
        new("newPlayer", Player, "CreateNewGamePlayer", true, "System.Void", "Source.Player.PersonalHistoryData", "System.Boolean"),
        new("scenes", Scenes, "LoadScenesOnStartGame", false, "System.Void"),
        new("menu", Scenes, "StartMenu", false, "System.Void"),
        new("splash", Scenes, "SplashScreen", false, "System.Void"),
        new("gameplay", "GameplayManager", "Start", false, "System.Void"),
        new("load", File, "LoadSaveGame", false, "System.Void")
    };
    internal const string TravelManager = "Behaviour.Managers.TravelManager";
    internal const string SpaceStationInterior = "Behaviour.UI.Spacestation.SpaceStationInterior";
    internal const string DockingOption = "Behaviour.Spacestation.Docking.DockingOption";
    // Global-namespace native type (verified against the inspected assembly).
    internal const string SpacestationExterior = "SpacestationExteriorManager";
    internal static readonly MethodBinding[] Travel =
    {
        new("route", TravelManager, "SetRouteToPOI", false, "System.Boolean", "Source.Galaxy.MapPointOfInterest"),
        new("cancel", TravelManager, "CancelTravel", false, "System.Boolean", "System.Nullable`1<UnityEngine.Vector2>"),
        new("unloadDeparted", TravelManager, "UnloadCurrentScene", false, "System.Void"),
        new("jumpGate", TravelManager, "JumpToSystem", false, "System.Collections.IEnumerator", "Source.Galaxy.POI.JumpGate"),
        new("jumpWormhole", TravelManager, "JumpToWormhole", false, "System.Collections.IEnumerator", "Source.Galaxy.POI.Wormhole"),
        new("travelNextWaypoint", TravelManager, "TravelToNextWaypoint", false, "System.Void"),
        new("inSystemWarp", TravelManager, "TravelInSystem", false, "System.Collections.IEnumerator"),
        // The genuine docking-request scope and the actual assignment inside it: intent, not scene
        // state, distinguishes an arrival/HUD/idle dock from init/reinit/relink/NPC assignments.
        new("dockRequest", SpacestationExterior, "CheckForDocking", false, "System.Void"),
        new("dockAssign", DockingOption, "AssignSpaceshipForDocking", false, "System.Void", "Behaviour.Unit.SpaceShip", "System.Boolean"),
        new("dock", DockingOption, "Dock", false, "System.Collections.IEnumerator", "System.Boolean"),
        new("undock", DockingOption, "Undock", false, "System.Collections.IEnumerator"),
        new("emergencyUndock", DockingOption, "EmergencyUndock", false, "System.Void"),
        new("interiorAwake", SpaceStationInterior, "Awake", false, "System.Void"),
        new("interiorStart", SpaceStationInterior, "Start", false, "System.Void"),
        new("interiorDestroy", SpaceStationInterior, "OnDestroy", false, "System.Void")
    };
    internal const string BoardingManager = "Behaviour.Managers.DungeonManager";
    internal const string BoardingOperation = "Behaviour.Dungeon.DungeonOperation";
    internal const string Boardable = "Behaviour.Unit.BoardableUnit";
    internal const string BoardingLocation = "Source.Data.Persistable.DungeonLocationData";
    internal const string BoardingOptions = "Source.Dungeon.DungeonOptions";
    internal static readonly MethodBinding[] BoardingQueries =
    {
        new("travel", BoardingManager, "IsTravelBlocked", true, "System.Boolean"),
        new("level", BoardingManager, "ExceedsLevelGap", true, "System.Boolean", "System.Int32"),
        new("crew", "Behaviour.UI.Dungeon.DungeonPanel", "HasAvailableCrew", true, "System.Boolean")
    };
    internal static readonly MethodBinding[] Boarding =
    {
        new("boardingInventoryDelivery", "Source.Item.Inventory", "Add", false, "Source.Item.Inventory/InventoryItem", "Behaviour.Item.InventoryItemType", "System.Int32", "System.Boolean", "System.Boolean"),
        new("boardingCreditDelivery", "Behaviour.Item.Usable.CreditsItem", "OnUse", false, "System.Boolean"),
        new("boardingWorldDelivery", "Source.Galaxy.MapPointOfInterest", "AddPersistable", false, "UnityEngine.GameObject", "Source.Data.Persistable.PersistableData"),
        new("boardingDataLoot", BoardingOperation, "HandleDataLootCollected", false, "System.Void", "Source.CompartmentSystem.SimLootEntry"),
        new("boardingShipReady", Boardable, "Start", false, "System.Void"),
        new("boardingLocationReady", "Behaviour.Unit.DungeonLocationUnit", "Start", false, "System.Void"),
        new("boardingStartShip", BoardingManager, "StartOperation", false, BoardingOperation, "Behaviour.Unit.SpaceShip", Boardable, BoardingOptions, "System.Boolean"),
        new("boardingStartLocation", BoardingManager, "StartOperation", false, BoardingOperation, "Behaviour.Unit.SpaceShip", BoardingLocation, BoardingOptions, "System.Boolean"),
        new("boardingResumeShip", BoardingManager, "ResumeOperation", false, BoardingOperation, Boardable, "System.Boolean"),
        new("boardingResumeLocation", BoardingManager, "ResumeOperation", false, BoardingOperation, "Behaviour.Unit.SpaceShip", BoardingLocation, "System.Boolean"),
        new("boardingTick", BoardingOperation, "Tick", false, "System.Void", "System.Single"),
        new("boardingCapture", Boardable, "FinalizeCapture", false, "System.Void"),
        new("boardingLoot", BoardingOperation, "TransferLootToCargo", false, "System.Void"),
        new("boardingPartialLoot", BoardingOperation, "TransferPartialLootToCargo", false, "System.Void"),
        new("boardingCrewDirect", BoardingOperation, "ReturnCrewToShip", false, "System.Void", "Source.Dungeon.DungeonSimulation"),
        new("boardingCrewPod", BoardingOperation, "HandlePodCrewReturned", false, "System.Void", "Behaviour.Persistables.BoardingPod")
    };
    internal const string Mission = "Source.MissionSystem.Mission";
    internal static readonly MethodBinding[] Missions =
    {
        new("missionArchive", Player, "ArchiveMission", false, "System.Void", "System.String", "System.Boolean"),
        new("missionRemove", Player, "RemoveMission", false, "System.Void", Mission, "System.Boolean"),
        new("missionStart", Mission, "OnMissionStart", false, "System.Void"),
        new("missionAccept", Player, "AddMissionWithLog", false, "System.Void", Mission, "System.Boolean"),
        new("missionClaim", Mission, "ClaimRewards", false, "System.Void", "System.Boolean"),
        new("missionFail", Mission, "MissionFailed", false, "System.Void", "System.String"),
        new("missionSweepIndustryWave", "Source.MissionSystem.IndustryMission", "ClaimRewards", false, "System.Void", "System.Boolean"),
        new("missionSweepPatrolWave", "Source.MissionSystem.PatrolMission", "ClaimRewards", false, "System.Void", "System.Boolean"),
        new("missionSweepBountyLaunch", "Behaviour.UI.Spacestation.Location.BountyBoard", "LaunchClicked", false, "System.Void"),
        new("missionSweepPatrolLaunch", "Behaviour.UI.Spacestation.Location.PatrolBoard", "LaunchClicked", false, "System.Void"),
        new("missionSweepIndustryLaunch", "Behaviour.UI.Spacestation.Location.IndustryBoard", "LaunchClicked", false, "System.Void"),
        new("missionSweepTutorialClear", Player, "TransitionTutorialToSandbox", false, "System.Void")
    };
    internal const string MissionObjective = "Source.MissionSystem.MissionObjective";
    /// <summary>
    /// Every way an API-owned mission can advance or pay out. These are load-safety guards, bound
    /// independently of the story module, because an orphaned owned mission is dangerous exactly when
    /// that module is absent, disabled or unbound.
    /// </summary>
    internal static readonly MethodBinding[] StoryProtection =
    {
        new("storyGuardUpdate", Mission, "Update", false, "System.Void", "System.Single"),
        new("storyGuardClaim", Mission, "ClaimRewards", false, "System.Void", "System.Boolean"),
        new("storyGuardComplete", Player, "CompleteMission", false, "System.Void", Mission, "System.Boolean"),
        new("storyGuardFail", Mission, "MissionFailed", false, "System.Void", "System.String"),
        new("storyGuardRetry", Mission, "RetryAsNextMission", false, "System.Void", "System.String"),
        // The button the player actually presses: remove, then re-add the same story identifier.
        new("storyGuardAbandon", "Behaviour.UI.Missions.MissionDetails", "AbandonMission", false, "System.Void", Mission),
        new("storyGuardTrigger", MissionObjective, "ProcessMissionTrigger", false, "System.Void", "Source.MissionSystem.MissionTrigger", "System.Object"),
        new("storyGuardScriptedTrigger", "Source.MissionSystem.Objectives.TriggerObjective", "ProcessMissionTrigger", false, "System.Void", "Source.MissionSystem.MissionTrigger", "System.Object")
    };
    internal static readonly MethodBinding[] MissionSnapshots =
    {
        new("missionSnapshot", Save, "SaveCurrentState", true, "LightJson.JsonObject")
    };
    internal static readonly MethodBinding[] Saves =
    {
        new("writeFile", Save, "WriteSaveFile", true, "System.Void", "System.IO.FileInfo", "LightJson.JsonObject", "Source.Util.SaveGameFormat"),
        new("writeMetadata", Save, "WriteVersionMetadata", true, "System.Void", "System.String", "System.String"),
        new("storeFailure", Save, "HandleStoreFailure", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32", "System.IO.FileInfo", "System.Exception"),
        new("store", Save, "Store", true, "System.Void", "LightJson.JsonObject", "System.String", "Source.Util.SaveGameFormat", "System.Int32")
    };
}
