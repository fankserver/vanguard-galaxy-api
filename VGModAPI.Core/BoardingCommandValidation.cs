using System.Collections.Generic;

namespace VGModAPI.Core;

/// <summary>Execution-time facts copied by the inspected adapter, not caller-supplied authorization.</summary>
internal sealed class BoardingCommandState
{
    internal bool TargetAlive, ShipAvailable, Travelling, Enterable, LevelAllowed, HasFactionConsequences;
    internal bool HasOperation, HasSavedSimulation, SimulationComplete, HasSimulation, Retreating, Victory, AwaitingExtraction, CanRequestExtraction;
    internal BoardingPhase Phase;
    internal BoardingHandle? Operation;
    internal int CrewCapacity;
    internal IDictionary<string, int> Crew = new Dictionary<string, int>();
    internal ISet<string> AllowedCrew = new HashSet<string>();
}

internal static class BoardingCommandValidation
{
    internal static BoardingCommandStatus Validate(BoardingCommandState state, BoardingCommandKind command,
        BoardingCrewManifest? crew, BoardingCommandOptions? options, bool allowFactionConsequences)
    {
        if (!state.TargetAlive || !state.ShipAvailable) return BoardingCommandStatus.TargetUnavailable;
        if (state.Travelling) return BoardingCommandStatus.Travelling;
        if (command == BoardingCommandKind.Start)
        {
            if (state.HasOperation) return BoardingCommandStatus.OperationExists;
            if (!state.Enterable || !state.LevelAllowed || state.HasSavedSimulation) return BoardingCommandStatus.WrongPhase;
            if (state.HasFactionConsequences && !allowFactionConsequences) return BoardingCommandStatus.FactionConsentRequired;
            if (options == null) return BoardingCommandStatus.InvalidOptions;
            return ValidateCrew(state, crew);
        }
        if (command == BoardingCommandKind.Resume)
            return state.HasOperation || (state.HasSavedSimulation && !state.SimulationComplete) ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
        if (!state.HasOperation) return BoardingCommandStatus.WrongPhase;
        switch (command)
        {
            case BoardingCommandKind.CancelApproach:
                return state.Phase is BoardingPhase.Approaching or BoardingPhase.AwaitingLanding ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingCommandKind.Reinforce:
                if (!state.HasSimulation || state.SimulationComplete || state.Retreating || state.Phase is not (BoardingPhase.Approaching or BoardingPhase.AwaitingLanding or BoardingPhase.Active)) return BoardingCommandStatus.WrongPhase;
                return ValidateCrew(state, crew);
            case BoardingCommandKind.Retreat:
                return state.HasSimulation && !state.SimulationComplete && !state.Retreating ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingCommandKind.RequestExtraction:
                return state.HasSimulation && state.Victory && state.CanRequestExtraction && !state.AwaitingExtraction && !state.Retreating && !state.SimulationComplete ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingCommandKind.ConfirmExtraction:
                return state.HasSimulation && state.Victory && state.AwaitingExtraction && !state.Retreating && !state.SimulationComplete ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            case BoardingCommandKind.SetOptions:
                if (options == null) return BoardingCommandStatus.InvalidOptions;
                return !state.Retreating && !state.SimulationComplete && state.Phase is BoardingPhase.Approaching or BoardingPhase.AwaitingLanding or BoardingPhase.Active ? BoardingCommandStatus.Admitted : BoardingCommandStatus.WrongPhase;
            default: return BoardingCommandStatus.WrongPhase;
        }
    }
    private static BoardingCommandStatus ValidateCrew(BoardingCommandState state, BoardingCrewManifest? crew) => crew == null
        ? BoardingCommandStatus.InvalidCrew : BoardingCrewTransfer.Validate(state.Crew, crew, state.AllowedCrew.Contains, state.CrewCapacity);
}
