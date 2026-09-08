using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BoardingTacticalValidationTests
{
    private static BoardingTacticalState Room() => new() { Active = true, HasCompartment = true, Discovered = true, Capacity = 3, EligibleCrew = 3 };
    [Fact]
    public void MovementChecksVisibilityCapacityAndSpecialistBeforeNativeOrder()
    {
        var state = Room(); var request = new BoardingTacticalRequest(BoardingTacticalAction.Move, 0, count: 2);
        Assert.Equal(BoardingCommandStatus.Admitted, BoardingTacticalValidation.Validate(state, request));
        state.Discovered = false; Assert.Equal(BoardingCommandStatus.NotDiscovered, BoardingTacticalValidation.Validate(state, request));
        state.Discovered = true; state.Capacity = 1; Assert.Equal(BoardingCommandStatus.CapacityExceeded, BoardingTacticalValidation.Validate(state, request));
        state.Capacity = 3; state.Sealed = true; Assert.Equal(BoardingCommandStatus.MissingSpecialist, BoardingTacticalValidation.Validate(state, request));
        state.AdjacentSpecialist = true; Assert.Equal(BoardingCommandStatus.Admitted, BoardingTacticalValidation.Validate(state, request));
    }
    [Fact]
    public void GrenadesRequireResourcesAndExplicitFriendlyDamageConsent()
    {
        var state = Room(); state.CanGrenade = true; state.GrenadeCharges = 1; state.FriendlyCrew = 1;
        Assert.Equal(BoardingCommandStatus.FriendlyDamageConsentRequired, BoardingTacticalValidation.Validate(state, new(BoardingTacticalAction.ThrowGrenade, 0)));
        var request = new BoardingTacticalRequest(BoardingTacticalAction.ThrowGrenade, 0, allowFriendlyDamage: true);
        Assert.Equal(BoardingCommandStatus.Admitted, BoardingTacticalValidation.Validate(state, request));
        state.GrenadeCooldown = 1; Assert.Equal(BoardingCommandStatus.InsufficientResources, BoardingTacticalValidation.Validate(state, request));
    }
    [Fact]
    public void BuyoutRequiresAnActualCandidateBeforeSpending()
    {
        var state = Room(); state.BuyoutPending = true; state.Credits = 100; state.BuyoutCost = 10;
        var request = new BoardingTacticalRequest(BoardingTacticalAction.AcceptBuyout);
        Assert.Equal(BoardingCommandStatus.WrongPhase, BoardingTacticalValidation.Validate(state, request));
        state.BuyoutCandidate = true; Assert.Equal(BoardingCommandStatus.Admitted, BoardingTacticalValidation.Validate(state, request));
        state.Credits = 9; Assert.Equal(BoardingCommandStatus.InsufficientResources, BoardingTacticalValidation.Validate(state, request));
    }
    [Fact]
    public void UnlockRequiresAvailableAdjacentSpecialistAndNoExistingTimer()
    {
        var state = Room(); state.Locked = true; var request = new BoardingTacticalRequest(BoardingTacticalAction.Unlock, 0);
        Assert.Equal(BoardingCommandStatus.MissingSpecialist, BoardingTacticalValidation.Validate(state, request));
        state.AdjacentSpecialist = true; Assert.Equal(BoardingCommandStatus.Admitted, BoardingTacticalValidation.Validate(state, request));
        state.UnlockInProgress = true; Assert.Equal(BoardingCommandStatus.WrongPhase, BoardingTacticalValidation.Validate(state, request));
    }
    [Theory]
    [InlineData(BoardingTacticalAction.ThrowGrenade)]
    [InlineData(BoardingTacticalAction.AcceptBuyout)]
    [InlineData(BoardingTacticalAction.ToggleBarricade)]
    [InlineData(BoardingTacticalAction.ConfirmExtraction)]
    public void TerminalEncountersRejectActions(BoardingTacticalAction action)
    {
        var state = Room(); state.Active = false;
        Assert.Equal(BoardingCommandStatus.WrongPhase, BoardingTacticalValidation.Validate(state, new(action, 0)));
    }
}
