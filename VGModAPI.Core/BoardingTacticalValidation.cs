namespace VGModAPI.Core;

/// <summary>Facts collected from the current native action path, never accepted from the consumer.</summary>
internal sealed class BoardingTacticalState
{
    internal bool Active, HasCompartment, Discovered, Destroyed, Locked, Sealed;
    internal bool UnlockInProgress, AdjacentSpecialist, CanBarricade, BarricadeHeld, CanGrenade;
    internal bool BuyoutPending, BuyoutCandidate, CanRequestExtraction, AwaitingExtraction, Victory;
    internal int Capacity, EligibleCrew, FriendlyCrew, GrenadeCharges;
    internal float GrenadeCooldown;
    internal long Credits, BuyoutCost;
}

internal static class BoardingTacticalValidation
{
    internal static BoardingCommandStatus Validate(BoardingTacticalState state, BoardingTacticalRequest request)
    {
        if (!state.Active) return BoardingCommandStatus.WrongPhase;
        var roomAction = request.Action is BoardingTacticalAction.Move or BoardingTacticalAction.ClearMovement
            or BoardingTacticalAction.RetreatFromCompartment or BoardingTacticalAction.SetPriority or BoardingTacticalAction.Unlock
            or BoardingTacticalAction.ToggleBarricade or BoardingTacticalAction.ThrowGrenade;
        if (roomAction)
        {
            if (!request.Compartment.HasValue || !state.HasCompartment) return BoardingCommandStatus.InvalidAction;
            if (!state.Discovered) return BoardingCommandStatus.NotDiscovered;
            if (state.Destroyed) return BoardingCommandStatus.TargetUnavailable;
        }
        switch (request.Action)
        {
            case BoardingTacticalAction.Move:
                if (state.Locked) return state.EligibleCrew > 0 ? BoardingCommandStatus.Admitted : BoardingCommandStatus.MissingSpecialist;
                if (state.Sealed && !state.AdjacentSpecialist) return BoardingCommandStatus.MissingSpecialist;
                if (request.Count > state.Capacity) return BoardingCommandStatus.CapacityExceeded;
                return request.Count <= state.EligibleCrew ? BoardingCommandStatus.Admitted : BoardingCommandStatus.InsufficientCrew;
            case BoardingTacticalAction.Unlock:
                if (!state.Locked || state.UnlockInProgress) return BoardingCommandStatus.WrongPhase;
                return state.AdjacentSpecialist ? BoardingCommandStatus.Admitted : BoardingCommandStatus.MissingSpecialist;
            case BoardingTacticalAction.ToggleBarricade:
                return state.CanBarricade || state.BarricadeHeld ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingTacticalAction.ThrowGrenade:
                if (state.FriendlyCrew > 0 && !request.AllowFriendlyDamage) return BoardingCommandStatus.FriendlyDamageConsentRequired;
                if (state.GrenadeCharges <= 0 || state.GrenadeCooldown > 0) return BoardingCommandStatus.InsufficientResources;
                return state.CanGrenade ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingTacticalAction.AcceptBuyout:
                if (!state.BuyoutPending || !state.BuyoutCandidate) return BoardingCommandStatus.WrongPhase;
                return state.BuyoutCost >= 0 && state.Credits >= state.BuyoutCost ? BoardingCommandStatus.Admitted : BoardingCommandStatus.InsufficientResources;
            case BoardingTacticalAction.DeclineBuyout:
                return state.BuyoutPending ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingTacticalAction.RequestExtraction:
                return state.Victory && state.CanRequestExtraction && !state.AwaitingExtraction ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingTacticalAction.ConfirmExtraction:
                return state.Victory && state.AwaitingExtraction ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingTacticalAction.RetreatFromCompartment:
                return state.FriendlyCrew > 0 ? BoardingCommandStatus.Admitted : BoardingCommandStatus.InsufficientCrew;
            case BoardingTacticalAction.SetPriority:
            case BoardingTacticalAction.ClearPriority:
            case BoardingTacticalAction.ClearMovement:
                return BoardingCommandStatus.Admitted;
            default: return BoardingCommandStatus.InvalidAction;
        }
    }
}
