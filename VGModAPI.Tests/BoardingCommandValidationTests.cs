using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingCommandValidationTests
{
    private static BoardingCommandState Ready() => new()
    {
        TargetAlive = true, ShipAvailable = true, Enterable = true, LevelAllowed = true,
        CrewCapacity = 5, Crew = new Dictionary<string, int> { ["Marine"] = 3 }, AllowedCrew = new HashSet<string> { "Marine" }
    };
    private static BoardingCrewManifest Crew() => new(new Dictionary<string, int> { ["Marine"] = 2 });
    [Fact]
    public void StartRequiresExplicitFactionConsentAndCurrentRoster()
    {
        var state = Ready(); state.HasFactionConsequences = true;
        Assert.Equal(BoardingCommandStatus.FactionConsentRequired, BoardingCommandValidation.Validate(state, BoardingCommandKind.Start, Crew(), new(), false));
        Assert.Equal(BoardingCommandStatus.Admitted, BoardingCommandValidation.Validate(state, BoardingCommandKind.Start, Crew(), new(), true));
        state.Crew["Marine"] = 1;
        Assert.Equal(BoardingCommandStatus.InsufficientCrew, BoardingCommandValidation.Validate(state, BoardingCommandKind.Start, Crew(), new(), true));
    }
    [Fact]
    public void ConfirmExtractionRequiresVictoryAndActualPendingExtraction()
    {
        var state = Ready(); state.HasOperation = state.HasSimulation = true; state.Phase = BoardingPhase.Active;
        Assert.Equal(BoardingCommandStatus.WrongPhase, BoardingCommandValidation.Validate(state, BoardingCommandKind.ConfirmExtraction, null, null, false));
        state.Victory = true; state.AwaitingExtraction = true;
        Assert.Equal(BoardingCommandStatus.Admitted, BoardingCommandValidation.Validate(state, BoardingCommandKind.ConfirmExtraction, null, null, false));
        state.SimulationComplete = true;
        Assert.Equal(BoardingCommandStatus.WrongPhase, BoardingCommandValidation.Validate(state, BoardingCommandKind.ConfirmExtraction, null, null, false));
    }
    [Theory]
    [InlineData(BoardingPhase.Approaching, true)]
    [InlineData(BoardingPhase.AwaitingLanding, true)]
    [InlineData(BoardingPhase.Active, true)]
    [InlineData(BoardingPhase.Extracting, false)]
    [InlineData(BoardingPhase.Resolved, false)]
    public void ReinforcementsRespectTransportAndResolutionPhases(BoardingPhase phase, bool admitted)
    {
        var state = Ready(); state.HasOperation = true; state.Phase = phase;
        Assert.Equal(admitted, BoardingCommandValidation.Validate(state, BoardingCommandKind.Reinforce, Crew(), null, false) == BoardingCommandStatus.Admitted);
        state.TargetAlive = false;
        Assert.Equal(BoardingCommandStatus.TargetUnavailable, BoardingCommandValidation.Validate(state, BoardingCommandKind.Reinforce, Crew(), null, false));
    }
}
