namespace VGModAPI.Core;

internal static class BoardingTacticalBindings
{
    internal const string Sim = "Source.Dungeon.DungeonSimulation";
    private const string Crew = "Source.CompartmentSystem.SimCrewUnit";
    private const string Filter = "Source.CompartmentSystem.MovementOrderFilter";
    internal static readonly MethodBinding[] Actions =
    {
        new("tacticalDirectMove", Sim, "MoveCrewTo", false, "System.Boolean", "System.Collections.Generic.List`1<Source.CompartmentSystem.SimCrewUnit>", "System.Int32"),
        new("tacticalMove", Sim, "IssueMovementOrder", false, "System.Void", "System.Int32", Filter, "System.Int32"),
        new("tacticalClear", Sim, "ClearPlayerMovementOrders", false, "System.Void", "System.Int32"),
        new("tacticalRetreat", Sim, "RetreatFromCompartment", false, "System.Void", "System.Int32"),
        new("tacticalUnlock", Sim, "TryUnlockCompartment", false, "System.Boolean", "System.Int32", Crew),
        new("tacticalBarricade", Sim, "ToggleBarricade", false, "System.Boolean", "System.Int32"),
        new("tacticalGrenade", Sim, "ThrowGrenade", false, "System.Boolean", "System.Int32"),
        new("tacticalBuyout", Sim, "AcceptBuyOut", false, "System.Boolean"),
        new("tacticalDecline", Sim, "DeclineBuyOut", false, "System.Void"),
        new("tacticalRequestExtraction", Sim, "RequestExtraction", false, "System.Void"),
        new("tacticalConfirmExtraction", Sim, "ConfirmExtraction", false, "System.Void")
    };
    internal static readonly MethodBinding[] Queries =
    {
        new("tacticalEligible", Sim, "CountEligibleUnitsForMovementOrder", false, "System.Int32", "System.Int32", Filter),
        new("tacticalCapacity", Sim, "GetFriendlyCapacityAvailable", false, "System.Int32", "System.Int32"),
        new("tacticalCanBarricade", Sim, "CanStartBarricade", false, "System.Boolean", "System.Int32"),
        new("tacticalBarricadeHeld", Sim, "IsBarricadeHeld", false, "System.Boolean", "System.Int32"),
        new("tacticalCanGrenade", Sim, "CanThrowGrenade", false, "System.Boolean", "System.Int32"),
        new("tacticalCooldown", Sim, "GetGrenadeCooldownRemaining", false, "System.Single"),
        new("tacticalCandidate", Sim, "FindBuyOutCandidate", false, Crew),
        new("tacticalSpecialist", Sim, "IsAdvantagedCrew", false, "System.Boolean", Crew),
        new("tacticalUnlockTime", Sim, "GetUnlockTimeRemaining", false, "System.Single", "System.Int32"),
        new("tacticalAlive", Crew, "IsAlive", false, "System.Boolean")
    };
    internal static readonly (string Key, string Type, string Name, string ValueType)[] Members =
    {
        ("simulationOptions", Sim, "options", BindingCatalog.BoardingOptions),
        ("grenades", Sim, "grenadeCharges", "System.Int32"),
        ("buyoutPending", Sim, "buyOutPending", "System.Boolean"),
        ("buyoutCost", Sim, "buyOutCost", "System.Int32"),
        ("priority", BindingCatalog.BoardingOptions, "priorityCompartmentIndex", "System.Nullable`1<System.Int32>"),
        ("adjacent", "Source.CompartmentSystem.SimCompartmentData", "adjacentIndices", "System.Collections.Generic.List`1<System.Int32>"),
        ("sealed", "Source.CompartmentSystem.SimCompartmentData", "isSealedByLockdown", "System.Boolean"),
        ("transit", Crew, "isInTransit", "System.Boolean"),
        ("friendly", Crew, "isFriendly", "System.Boolean"),
        ("directiveTarget", Crew, "assignedDirectiveTarget", "System.Int32")
    };
}
