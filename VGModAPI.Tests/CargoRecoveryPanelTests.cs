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
        var boarding = Fake<IBoardingEvents>((method, _) => method switch
        { "Subscribe" => observerLease, "GetOperations" => new[] { state }, _ => throw new InvalidOperationException(method) });
        var settlement = Fake<IDungeonSettlement>((method, args) =>
        { Assert.Equal("Subscribe", method); receive = (Action<DungeonSettlementSnapshot>)args[1]!; return settlementLease; });
        var panel = Fake<IDungeonPanelApi>((method, _) =>
        { Assert.Equal("RegisterAction", method); if (failRegistration) throw new InvalidOperationException("registration refused"); return panelLease; });
        CargoRecoveryPanel Create() => new("cargo", target, panel, boarding,
            Fake<IBoardingCommands>((_, _) => throw new InvalidOperationException("Unexpected command")),
            Fake<IBoardingTactics>((_, _) => throw new InvalidOperationException("Unexpected tactic")),
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
