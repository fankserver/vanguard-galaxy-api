using System;
using System.Collections.Generic;
using System.Reflection;
using AuthoredDungeon;
using VGModAPI;
using Xunit;
namespace VGModAPI.Tests;

public sealed class CargoRecoveryPanelTests
{
    // Test-only interface stubs; the consumer example itself contains no reflection.
    public class Stub : DispatchProxy
    {
        public Func<string, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args!);
    }
    private static T Fake<T>(Func<string, object?[], object?> call) where T : class
    { var result = DispatchProxy.Create<T, Stub>(); ((Stub)(object)result).Call = call; return result; }
    private sealed class Lease : IDisposable
    { internal bool Disposed; public void Dispose() => Disposed = true; }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ActionChecksEligibilityReportsConflictAndReleasesAcquiredControl(bool admitted, bool failTactic)
    {
        var session = Guid.NewGuid(); var target = new BoardingHandle(session, Guid.NewGuid()); var operation = new BoardingHandle(session, Guid.NewGuid());
        var state = new BoardingOperationSnapshot(operation, target, 1, (BoardingPhase)0, false, false, null, null, null,
            Array.Empty<KeyValuePair<string, int>>(), Array.Empty<BoardingCompartmentSnapshot>(), null);
        var view = new DungeonPanelSnapshot(Guid.NewGuid(), 1, new(target, 1, BoardingEncounterKind.Ship, "Target", null, null, BoardingAvailability.OperationActive, operation), state);
        Func<DungeonPanelSnapshot, DungeonPanelAction?>? present = null; Action<DungeonPanelSnapshot>? activate = null;
        var panel = Fake<IDungeonPanelApi>((_, args) => { Assert.Equal("cargo-extraction-" + target.Generation.ToString("N"), args[1]); present = (Func<DungeonPanelSnapshot, DungeonPanelAction?>)args[2]!; activate = (Action<DungeonPanelSnapshot>)args[3]!; return new Lease(); });
        var boarding = Fake<IBoardingService>((name, _) => name switch { "add_Changed" or "remove_Changed" => null, "GetOperations" => Array.Empty<BoardingOperationSnapshot>(), "GetOperation" => null, _ => throw new InvalidOperationException(name) });
        var settlement = Fake<IDungeonSettlement>((_, _) => new Lease());
        var disposed = false; var executed = false; var eligible = false;
        var controller = Fake<IBoardingController>((name, _) => { Assert.Equal("Dispose", name); disposed = true; return null; });
        var acquisition = new BoardingCommandResult(admitted ? BoardingCommandStatus.Admitted : BoardingCommandStatus.ControlConflict, "control");
        var commands = Fake<IBoardingCommandService>((_, args) => { args[2] = admitted ? controller : null; return acquisition; });
        var execution = new BoardingCommandResult(BoardingCommandStatus.Admitted, "request");
        var tactics = Fake<IBoardingTacticalService>((name, args) =>
        {
            if (name == "GetSnapshot") return new BoardingTacticalSnapshot(operation, Array.Empty<BoardingCompartmentSnapshot>(), 0, 0, eligible, false);
            executed = true; Assert.Same(controller, args[0]); Assert.Equal(BoardingTacticalAction.RequestExtraction, ((BoardingTacticalRequest)args[1]!).Action);
            if (failTactic) throw new InvalidOperationException("tactic failure"); return execution;
        });
        BoardingCommandResult? result = null;
        using var example = new CargoRecoveryPanel("cargo", target, panel, boarding, commands, tactics, settlement, value => result = value, _ => { });
        Assert.Null(present!(view)); eligible = true; Assert.NotNull(present(view));
        if (admitted && failTactic) Assert.Throws<InvalidOperationException>(() => activate!(view));
        else { activate!(view); Assert.Same(admitted ? execution : acquisition, result); }
        Assert.Equal(admitted, executed); Assert.Equal(admitted, disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettlementDoesNotDependOnPanelPresentationAndFailureReleasesSubscriptions(bool failRegistration)
    {
        var session = Guid.NewGuid(); var target = new BoardingHandle(session, Guid.NewGuid());
        var operation = new BoardingHandle(session, Guid.NewGuid());
        var state = new BoardingOperationSnapshot(operation, target, 1, (BoardingPhase)0, false, false, null, null, null,
            Array.Empty<KeyValuePair<string, int>>(), Array.Empty<BoardingCompartmentSnapshot>(), null);
        var observerLease = new Lease(); var settlementLease = new Lease(); var panelLease = new Lease();
        Action<DungeonSettlementSnapshot>? receive = null; var observed = 0;
        object? DisposeObserver() { observerLease.Dispose(); return null; }
        var boarding = Fake<IBoardingService>((method, _) => method switch
        { "add_Changed" => null, "remove_Changed" => DisposeObserver(), "GetOperations" => new[] { state }, "GetOperation" => null, _ => throw new InvalidOperationException(method) });
        var settlement = Fake<IDungeonSettlement>((method, args) =>
        { Assert.Equal("Subscribe", method); receive = (Action<DungeonSettlementSnapshot>)args[1]!; return settlementLease; });
        var panel = Fake<IDungeonPanelApi>((method, _) =>
        { Assert.Equal("RegisterAction", method); if (failRegistration) throw new InvalidOperationException("registration refused"); return panelLease; });
        CargoRecoveryPanel Create() => new("cargo", target, panel, boarding,
            Fake<IBoardingCommandService>((_, _) => throw new InvalidOperationException("Unexpected command")),
            Fake<IBoardingTacticalService>((_, _) => throw new InvalidOperationException("Unexpected tactic")),
            settlement, _ => throw new InvalidOperationException("Unexpected command receipt"), _ => observed++);
        if (failRegistration) Assert.Throws<InvalidOperationException>(() => Create());
        else
        {
            using var example = Create();
            // No presenter or activation callback was invoked; the native panel can remain closed.
            receive!(new(operation, "Victory", true, false, Array.Empty<KeyValuePair<string, int>>(), Array.Empty<KeyValuePair<string, int>>()));
            receive(new(new BoardingHandle(session, Guid.NewGuid()), "Victory", true, true, Array.Empty<KeyValuePair<string, int>>(), Array.Empty<KeyValuePair<string, int>>()));
            Assert.Equal(1, observed);
        }
        Assert.True(observerLease.Disposed); Assert.True(settlementLease.Disposed);
        Assert.Equal(!failRegistration, panelLease.Disposed);
    }
}
