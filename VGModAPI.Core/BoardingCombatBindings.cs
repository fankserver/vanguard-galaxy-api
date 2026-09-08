namespace VGModAPI.Core;

internal static class BoardingCombatBindings
{
    private const string Sim = BoardingTacticalBindings.Sim;
    private const string Crew = "Source.CompartmentSystem.SimCrewUnit";
    private const string Pool = "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCrewUnit>";
    private const string Manifest = "System.Collections.Generic.Dictionary`2<System.String,System.Int32>";
    internal static readonly MethodBinding[] Scopes =
    {
        new("combatTick", Sim, "Tick", false, "System.Void", "System.Single"),
        new("combatPlaceAttackers", Sim, "PlaceAttackers", false, "System.Void", Manifest),
        new("combatPlaceDefenders", Sim, "PlaceDefenders", false, "System.Void", "System.Single"),
        new("combatPlaceDungeon", Sim, "PlaceDefendersForDungeon", false, "System.Void", "Source.Dungeon.DungeonHostilityRating"),
        new("combatAddCrew", Sim, "AddCrew", false, "System.Void", Manifest),
        new("combatAddDefenders", Sim, "AddDefenders", false, "System.Void", Manifest, "System.String"),
        new("combatAddStaging", Sim, "AddAttackersToStaging", false, "System.Void", Manifest),
        new("combatEstimate", Sim, "EstimateOutcome", false, "System.ValueTuple`4<System.Single,System.String,System.Int32,System.Int32>")
    };
    internal static readonly MethodBinding[] Hooks =
    {
        new("combatPlayerReinforcements", "Behaviour.UI.Dungeon.DungeonPanel", "AddReinforcements", false, "System.Void"),
        new("combatPower", Crew, "EffectivePower", false, "System.Single", "System.Single", "System.Single"),
        new("combatHealth", Crew, "InitHp", false, "System.Void", "System.Single"),
        new("combatCasualties", Sim, "ApplyCasualties", false, "System.Int32", Pool, "System.Int32", "System.Single", "System.Boolean"),
        new("combatSurrender", Sim, "TrySurrenderInCombat", false, "System.Boolean", Crew, "System.Int32", "Source.CompartmentSystem.FactionBoardingProfile", "System.Single"),
        new("combatSideSwitch", Sim, "TrySideSwitch", false, "System.Void", "System.Single"),
        new("combatDefection", Sim, "TryDefectInCombat", false, "System.Boolean", Crew, "System.Int32", "System.Single", "System.Single"),
        new("combatMassSurrender", Sim, "CheckMassSurrender", false, "System.Boolean", "System.String"),
        new("combatAttackerCollapse", Sim, "CheckAttackerMoraleCollapse", false, "System.Boolean", "System.String"),
        new("combatReinforcements", Sim, "TickReinforcementSchedule", false, "System.Void"),
        new("combatHazard", Sim, "FireHazardEvent", false, "System.Void", "Source.CompartmentSystem.SimCompartmentData", "Source.CompartmentSystem.SimCompartmentEvent"),
        new("combatVent", Sim, "TryAirlockVent", false, "System.Void", "System.Single"),
        new("combatStructuralVent", Sim, "TriggerRandomVentDamage", false, "System.Boolean"),
        new("combatMoraleRecovery", Sim, "TickIdleMoraleRecovery", false, "System.Void", "System.Single"),
        new("combatMoraleGlobal", Sim, "TickGlobalDefenderMorale", false, "System.Void"),
        new("combatMoraleAttackers", Sim, "TickAttackerMorale", false, "System.Void", "System.Single", "System.Boolean", "System.Collections.Generic.HashSet`1<System.Int32>", "System.Single", "System.Boolean"),
        new("combatMoraleCombat", Sim, "TickCompartmentCombatMorale", false, "System.Void", "System.Int32", "System.Single")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("combatLevel", Sim, "dungeonLevel", "System.Int32"),
        ("combatKind", Sim, "dungeonType", "Source.Dungeon.DungeonType"),
        ("combatFriendly", Crew, "isFriendly", "System.Boolean"),
        ("combatMorale", Crew, "morale", "System.Single")
    };
}
