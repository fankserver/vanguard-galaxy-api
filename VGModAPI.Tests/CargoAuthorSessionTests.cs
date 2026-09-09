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
        internal IBoardingEvents Events => Fake<IBoardingEvents>((name, args) =>
        {
            if (name == "GetOperations") { Assert.NotEmpty(Boarding); return Seed; }
            if (name == "GetOperation") return null;
            Assert.Equal("Subscribe", name); var callback = (Action<BoardingEvent>)args[1]!; Boarding.Add(callback); return new Lease(() => Boarding.Remove(callback));
        });
        internal IDungeonContent Content => Fake<IDungeonContent>((name, _) =>
        {
            Assert.Equal("AcquireProvider", name);
            return Fake<IDungeonProvider>((method, args) =>
            {
                if (method == "Register") { RegisterCalls++; if (RejectDefinition) throw new ArgumentException("Catalog unavailable"); return new Lease(() => { }); }
                if (method == "Attach") { AttachCalls++; return new DungeonContentResult(DungeonContentStatus.TargetInUse, "already attached"); }
                Assert.Equal("Dispose", method); ProviderDisposals++; return null;
            });
        });
        internal IDungeonPanelApi Panel => Fake<IDungeonPanelApi>((name, args) =>
        {
            if (name == "get_Capabilities") return new DungeonPanelCapabilities(true, true, ContextualActions);
            Assert.Equal("RegisterAction", name); if (RejectAction) throw new InvalidOperationException("Rejected action"); var key = (string)args[1]!; Assert.False(Actions.ContainsKey(key)); Actions.Add(key, (Action<DungeonPanelSnapshot>)args[3]!); Presenters.Add(key, (Func<DungeonPanelSnapshot, DungeonPanelAction?>)args[2]!);
            return new Lease(() => { Actions.Remove(key); Presenters.Remove(key); });
        });
        internal CargoAuthorSession Create(bool optional = true, bool required = true) => new("item", Life, Events, required ? Content : null,
            optional ? Panel : null, Fake<IBoardingCommandService>((_, _) => throw new InvalidOperationException()), Fake<IBoardingTactics>((_, _) => throw new InvalidOperationException()),
            Fake<IDungeonSettlement>((name, _) => { Assert.Equal("Subscribe", name); SettlementLeases++; return new Lease(() => SettlementLeases--); }), Logs.Add);
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
    public void UnavailableRendererKeepsContentWithoutControls()
    { var f = new Fixture { ContextualActions = false }; using var author = f.Create(); Assert.Equal(1, f.RegisterCalls); Assert.Empty(f.Actions); }
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
