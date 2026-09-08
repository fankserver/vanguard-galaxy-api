namespace VGModAPI.Core;

internal static class DungeonNativeSchema
{
    internal const string Room = "Source.CompartmentSystem.SimCompartmentData";
    internal const string Loot = "Source.CompartmentSystem.SimLootEntry";
    internal const string Crew = "Source.CompartmentSystem.SimCrewUnit";
    internal const string Sim = "Source.Dungeon.DungeonSimulation";
    internal static readonly MethodBinding[] Methods =
    {
        new("dungeonEntered", BindingCatalog.BoardingOperation, "BeginWalkSimulation", false, "System.Void"),
        new("dungeonGuardTick", BindingCatalog.BoardingOperation, "Tick", false, "System.Void", "System.Single"),
        new("dungeonResumeShip", BindingCatalog.BoardingManager, "ResumeOperation", false, BindingCatalog.BoardingOperation, BindingCatalog.Boardable, "System.Boolean"),
        new("dungeonResumeLocation", BindingCatalog.BoardingManager, "ResumeOperation", false, BindingCatalog.BoardingOperation, "Behaviour.Unit.SpaceShip", BindingCatalog.BoardingLocation, "System.Boolean"),
        new("dungeonSerialization", BindingCatalog.Save, "SaveCurrentState", true, "LightJson.JsonObject"),
        new("dungeonWalkCreated", BindingCatalog.BoardingOperation, "InitialiseWalkSimulation", false, "System.Void", "Source.Dungeon.DungeonData"),
        new("dungeonCombatMode", Sim, "ApplyCombatMode", false, "System.Void"),
        new("dungeonCrewAlive", Crew, "IsAlive", false, "System.Boolean"),
        new("dungeonProfile", "Source.CompartmentSystem.FactionBoardingProfile", "GetById", true, "Source.CompartmentSystem.FactionBoardingProfile", "System.String"),
        new("dungeonNoScuttleProfile", Sim, "BuildNoScuttleProfile", true, "Source.CompartmentSystem.FactionBoardingProfile", "Source.CompartmentSystem.FactionBoardingProfile"),
        new("dungeonHazard", Sim, "FireHazardEvent", false, "System.Void", Room, "Source.CompartmentSystem.SimCompartmentEvent"),
        new("dungeonReinforcements", Sim, "TickReinforcementSchedule", false, "System.Void"),
        new("dungeonWalkLayout", BindingCatalog.BoardingOperation, "ApplyDefinitionToSim", false, "System.Void", Sim, "Source.Dungeon.DungeonData"),
        new("dungeonShipDefenders", Sim, "PlaceDefenders", false, "System.Void", "System.Single"),
        new("dungeonWalkDefenders", Sim, "PlaceDefendersForDungeon", false, "System.Void", "Source.Dungeon.DungeonHostilityRating"),
        new("dungeonItem", "Behaviour.Item.InventoryItemType", "TryGet", true, "System.Boolean", "System.String", "Behaviour.Item.InventoryItemType&"),
        new("dungeonFaction", "Source.Galaxy.Faction", "Get", true, "Source.Galaxy.Faction", "System.String"),
        new("dungeonLocationSave", BindingCatalog.BoardingLocation, "DataToJson", false, "System.Void", "LightJson.JsonObject"),
        new("dungeonLocationLoad", BindingCatalog.BoardingLocation, "LoadFromJson", false, "System.Void", "LightJson.JsonObject"),
        new("dungeonShipLayout", Sim, "InitBoardingLayout", true, "System.Void", Sim, BindingCatalog.BoardingLocation),
        new("dungeonRoomCapacity", Room, "EffectiveCapacity", false, "System.Int32", "System.Int32"),
        new("dungeonCrewHealth", Crew, "InitHp", false, "System.Void", "System.Single"),
        new("dungeonOccupants", Sim, "RecordOriginalOccupantCounts", false, "System.Void")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("authoredTransit", Crew, "isInTransit", "System.Boolean"),
        ("authoredIncapacitated", Crew, "IsIncapacitated", "System.Boolean"),
        ("authoredFaction", Sim, "factionId", "System.String"),
        ("authoredProfile", Sim, "factionProfile", "Source.CompartmentSystem.FactionBoardingProfile"),
        ("authoredNoScuttle", Sim, "shipNoScuttle", "System.Boolean"),
        ("authoredLootId", Loot, "itemTypeId", "System.String"),
        ("authoredLootAmount", Loot, "amount", "System.Int32"),
        ("authoredLootCollected", Loot, "isCollected", "System.Boolean"),
        ("authoredLootLevel", Loot, "level", "System.Int32"),
        ("authoredLootRoom", Loot, "compartmentIndex", "System.Int32"),
        ("authoredCollected", Sim, "collectedLoot", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimLootEntry>"),
        ("authoredCollected", Room, "collectedLoot", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimLootEntry>"),
        ("authoredLocationSize", BindingCatalog.BoardingLocation, "shipSizeTier", "Source.SpaceShip.SpaceShipType"),
        ("authoredLocationData", BindingCatalog.BoardingLocation, "dungeonData", "Source.Dungeon.DungeonData"),
        ("authoredSavedSimulation", "Source.Dungeon.DungeonData", "simulation", Sim),
        ("authoredHealth", Sim, "levelDefenderHpMod", "System.Single"),
        ("authoredRoomType", Room, "type", "Source.CompartmentSystem.CompartmentType"),
        ("authoredRoomIndex", Room, "index", "System.Int32"),
        ("authoredRoomState", Room, "state", "Source.CompartmentSystem.CompartmentState"),
        ("authoredRoomLocked", Room, "isLocked", "System.Boolean"),
        ("authoredRoomWasLocked", Room, "wasLocked", "System.Boolean"),
        ("authoredRoomNeighbors", Room, "adjacentIndices", "System.Collections.Generic.List`1<System.Int32>"),
        ("authoredCrewType", Crew, "crewTypeId", "System.String"),
        ("authoredCrewFriendly", Crew, "isFriendly", "System.Boolean"),
        ("authoredCrewRoom", Crew, "compartmentIndex", "System.Int32")
    };
}
