using System;
using System.Collections.Generic;
using System.Linq;
using DungeonAuthor;
using VGModAPI;
using Xunit;
namespace VGModAPI.Tests;
public sealed class CargoAuthorSessionTests
{
    private static T Fake<T>(Func<string, object?[], object?> call) where T : class
    { var value = System.Reflection.DispatchProxy.Create<T, CargoRecoveryPanelTests.Stub>(); ((CargoRecoveryPanelTests.Stub)(object)value).Call = call; return value; }
    private sealed class Lease : IDisposable
    { private Action? _dispose; internal Lease(Action dispose) => _dispose = dispose; public void Dispose() { var action = _dispose; _dispose = null; action?.Invoke(); } }
    private sealed class Fixture
    {
        internal readonly List<Action<LifecycleEvent>> Lifecycle = new();
        internal readonly List<Action<BoardingEvent>> Boarding = new();
        internal readonly Dictionary<string, Action<DungeonPanelSnapshot>> Actions = new();
        internal readonly List<string> Logs = new();
        internal readonly List<Action<DungeonSettlementSnapshot>> Settlements = new();
        internal int RegisterCalls, AttachCalls, ProviderDisposals, SettlementLeases;
        internal bool RejectDefinition, RejectAction;
        internal bool ContextualActions = true;
        internal BoardingOperationSnapshot[] Seed = Array.Empty<BoardingOperationSnapshot>();
        internal readonly Dictionary<string, Func<DungeonPanelSnapshot, DungeonPanelAction?>> Presenters = new();
        internal readonly Guid Session = Guid.NewGuid();
        internal ILifecycleService Life => Fake<ILifecycleService>((name, args) =>
        {
            var callback = (Action<LifecycleEvent>)args[0]!;
            if (name == "add_Changed") Lifecycle.Add(callback);
            else { Assert.Equal("remove_Changed", name); Lifecycle.Remove(callback); }
            return null;
        });
        internal IBoardingService Events => Fake<IBoardingService>((name, args) =>
        {
            if (name == "GetOperations") { Assert.NotEmpty(Boarding); return Seed; }
            if (name == "GetOperation") return null;
            var callback = (Action<BoardingEvent>)args[0]!;
            if (name == "add_Changed") Boarding.Add(callback);
            else { Assert.Equal("remove_Changed", name); Boarding.Remove(callback); }
            return null;
        });
        internal IDungeonContentService Content => Fake<IDungeonContentService>((name, _) =>
        {
            Assert.Equal("AcquireProvider", name);
            return Fake<IDungeonProvider>((method, args) =>
            {
                if (method == "Register") { RegisterCalls++; if (RejectDefinition) throw new ArgumentException("Catalog unavailable"); return new Lease(() => { }); }
                if (method == "Attach") { AttachCalls++; return new DungeonContentResult(DungeonContentStatus.TargetInUse, "already attached"); }
                Assert.Equal("Dispose", method); ProviderDisposals++; return null;
            });
        });
        internal IDungeonPanelService Panel => Fake<IDungeonPanelService>((name, args) =>
        {
            if (name == "get_Capabilities") return new DungeonPanelCapabilities(true, true, ContextualActions);
            Assert.Equal("RegisterAction", name); if (RejectAction) throw new InvalidOperationException("Rejected action"); var key = (string)args[1]!; Assert.False(Actions.ContainsKey(key)); Actions.Add(key, (Action<DungeonPanelSnapshot>)args[3]!); Presenters.Add(key, (Func<DungeonPanelSnapshot, DungeonPanelAction?>)args[2]!);
            return new Lease(() => { Actions.Remove(key); Presenters.Remove(key); });
        });
        internal CargoAuthorSession Create(bool optional = true, bool required = true) => new("item", Life, Events, required ? Content : null,
            optional ? Panel : null, Fake<IBoardingCommandService>((_, _) => throw new InvalidOperationException()), Fake<IBoardingTacticalService>((_, _) => throw new InvalidOperationException()),
            Fake<IDungeonSettlementService>((name, args) => { var handler = (Action<DungeonSettlementSnapshot>)args[0]!; if (name == "add_Changed") { SettlementLeases++; Settlements.Add(handler); } else { Assert.Equal("remove_Changed", name); SettlementLeases--; Settlements.Remove(handler); } return null; }), Logs.Add);
        internal BoardingEvent Event(BoardingHandle target, BoardingEventKind kind)
        {
            var operation = new BoardingHandle(Session, Guid.NewGuid());
            return new(1, kind, new(target, 1, BoardingEncounterKind.Ship, "Ship", null, null, BoardingAvailability.OperationActive, operation),
                new(operation, target, 1, (BoardingPhase)0, false, false, null, null, null, Array.Empty<KeyValuePair<string, int>>(), Array.Empty<BoardingCompartmentSnapshot>(), null));
        }
        internal void Emit(BoardingEvent fact) { foreach (var callback in Boarding.ToArray()) if (Boarding.Contains(callback)) callback(fact); }
        internal void Emit(LifecycleEventKind kind) { foreach (var callback in Lifecycle.ToArray()) callback(new(kind, new(Session, SessionPhase.GameplayInitialized, SessionOrigin.NewGame, null))); }
    }
    [Fact]
    public void TracksDistinctTargetsOnceAndReleasesRetiredAndInvalidatedTargets()
    {
        var f = new Fixture(); using var author = f.Create();
        var a = new BoardingHandle(f.Session, Guid.NewGuid()); var b = new BoardingHandle(f.Session, Guid.NewGuid());
        f.Emit(f.Event(a, BoardingEventKind.OperationStarted)); f.Emit(f.Event(a, BoardingEventKind.OperationResumed)); f.Emit(f.Event(b, BoardingEventKind.OperationStarted));
        Assert.Equal(3, f.Actions.Count); Assert.Equal(2, f.SettlementLeases); Assert.Equal(0, f.AttachCalls);
        f.Emit(f.Event(a, BoardingEventKind.OperationRetired)); Assert.Equal(3, f.Actions.Count);
        f.Emit(f.Event(a, BoardingEventKind.Retired)); Assert.Equal(2, f.Actions.Count); Assert.Equal(1, f.SettlementLeases);
        f.Emit(LifecycleEventKind.SessionInvalidated); Assert.Single(f.Actions); Assert.Equal(0, f.SettlementLeases); Assert.Equal(1, f.RegisterCalls);
        var fact = f.Event(b, BoardingEventKind.OperationStarted);
        Assert.Null(f.Presenters["attach-cargo"](new(Guid.NewGuid(), 1, fact.Target, fact.Operation)));
        var idle = new DungeonPanelSnapshot(Guid.NewGuid(), 1, new(b, 1, BoardingEncounterKind.Ship, "Ship", null, null, BoardingAvailability.Available, null), null);
        Assert.Equal("Attach cargo encounter", f.Presenters["attach-cargo"](idle)!.Label);
        f.Actions["attach-cargo"](idle); Assert.Equal(1, f.AttachCalls); Assert.Contains("Cargo attach: TargetInUse", f.Logs);
        author.Dispose(); Assert.Empty(f.Actions); Assert.Empty(f.Boarding); Assert.Empty(f.Lifecycle); Assert.Equal(1, f.ProviderDisposals);
    }
    [Fact]
    public void RemovedHostKeepsSettlementObservationUntilLastReturningOperationRetires()
    {
        var f = new Fixture(); using var author = f.Create();
        var target = new BoardingHandle(f.Session, Guid.NewGuid());
        var first = f.Event(target, BoardingEventKind.OperationStarted);
        var second = f.Event(target, BoardingEventKind.OperationStarted);
        f.Seed = new[] { first.Operation!, second.Operation! };
        f.Emit(first); f.Emit(second); f.Emit(f.Event(target, BoardingEventKind.Retired));
        Assert.Equal(1, f.SettlementLeases);
        f.Seed = new[] { second.Operation! };
        f.Emit(new BoardingEvent(2, BoardingEventKind.OperationRetired, first.Target, first.Operation));
        Assert.Equal(1, f.SettlementLeases);
        var settlement = new DungeonSettlementSnapshot(second.Operation!.Handle, "FriendlyExtracted", false, true,
            Array.Empty<KeyValuePair<string, int>>(), Array.Empty<KeyValuePair<string, int>>(), true);
        foreach (var handler in f.Settlements.ToArray()) handler(settlement);
        Assert.Contains(f.Logs, message => message.Contains("crew return settled=True"));
        f.Seed = Array.Empty<BoardingOperationSnapshot>();
        f.Emit(new BoardingEvent(3, BoardingEventKind.OperationRetired, second.Target, second.Operation));
        Assert.Equal(0, f.SettlementLeases); Assert.Single(f.Actions);
    }
    [Fact]
    public void UnavailableRendererKeepsContentWithoutControls()
    { var f = new Fixture { ContextualActions = false }; using var author = f.Create(); Assert.Equal(1, f.RegisterCalls); Assert.Empty(f.Actions); }
    [Fact]
    public void OptionalPresentationCanBecomeReadyAfterContentWithoutReregisteringIt()
    {
        var f = new Fixture { ContextualActions = false }; using var author = f.Create();
        Assert.Equal(1, f.RegisterCalls); Assert.Empty(f.Actions);
        f.Emit(LifecycleEventKind.GameplayInitialized); Assert.Empty(f.Actions);
        f.ContextualActions = true;
        f.Seed = new[] { f.Event(new(f.Session, Guid.NewGuid()), BoardingEventKind.OperationStarted).Operation! };
        f.Emit(LifecycleEventKind.GameplayInitialized);
        Assert.Equal(1, f.RegisterCalls); Assert.Equal(2, f.Actions.Count); Assert.Equal(1, f.SettlementLeases);
        f.Emit(LifecycleEventKind.GameplayInitialized);
        Assert.Equal(1, f.RegisterCalls); Assert.Equal(2, f.Actions.Count); Assert.Equal(1, f.SettlementLeases);
    }
    [Fact]
    public void SeedsExistingOperationsAndDoesNotReregisterSuccessfulDefinition()
    {
        var f = new Fixture(); f.Seed = new[] { f.Event(new(f.Session, Guid.NewGuid()), BoardingEventKind.OperationStarted).Operation! };
        using var author = f.Create(); Assert.Equal(2, f.Actions.Count);
        f.Emit(LifecycleEventKind.GameplayInitialized); Assert.Equal(1, f.RegisterCalls);
    }
    [Fact]
    public void FailedActionRegistrationReleasesSessionResources()
    {
        var f = new Fixture { RejectAction = true }; Assert.Throws<InvalidOperationException>(() => f.Create());
        Assert.Empty(f.Lifecycle); Assert.Empty(f.Boarding); Assert.Empty(f.Actions); Assert.Equal(1, f.ProviderDisposals);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapabilityAbsenceNeverCreatesControls(bool required)
    { var f = new Fixture(); using var author = f.Create(optional: false, required: required); Assert.Equal(required ? 1 : 0, f.RegisterCalls); Assert.Empty(f.Actions); Assert.Empty(f.Boarding); }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CatalogFailureRetriesOnlyOnceAtGameplayReadiness(bool recover)
    {
        var f = new Fixture { RejectDefinition = true }; using var author = f.Create(); Assert.Equal(1, f.RegisterCalls);
        f.RejectDefinition = !recover; f.Emit(LifecycleEventKind.GameplayInitialized); f.Emit(LifecycleEventKind.GameplayInitialized);
        Assert.Equal(2, f.RegisterCalls); Assert.Equal(recover ? 1 : 0, f.Actions.Count);
        if (recover) { f.Emit(f.Event(new(f.Session, Guid.NewGuid()), BoardingEventKind.OperationStarted)); Assert.Equal(2, f.Actions.Count); }
    }
}
