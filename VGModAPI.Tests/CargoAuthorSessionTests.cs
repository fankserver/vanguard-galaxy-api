using System;
using System.Collections.Generic;
using System.Linq;
using CargoRecovery;
using VGModAPI;
using Xunit;
namespace VGModAPI.Tests;

/// <summary>
/// The example is isolated: it must only ever touch the derelict it authored itself. These tests
/// pin that contract — no contextual action on an arbitrary target, no attach to a vanilla
/// encounter, and no command/settlement lease for an operation it did not create.
/// </summary>
public sealed class CargoAuthorSessionTests
{
    private static T Fake<T>(Func<string, object?[], object?> call) where T : class
    { var value = System.Reflection.DispatchProxy.Create<T, CargoEncounterPanelTests.Stub>(); ((CargoEncounterPanelTests.Stub)(object)value).Call = call; return value; }
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
        internal bool RejectDefinition;
        internal bool ContextualActions = true;
        internal BoardingOperationSnapshot[] Seed = Array.Empty<BoardingOperationSnapshot>();
        internal BoardingTargetSnapshot[] Targets = Array.Empty<BoardingTargetSnapshot>();
        internal DungeonContentStatus AttachStatus = DungeonContentStatus.StaleTarget;
        internal readonly Dictionary<string, Func<DungeonPanelSnapshot, DungeonPanelAction?>> Presenters = new();
        internal readonly Guid Session = Guid.NewGuid();
        internal ILifecycleService Life => Fake<ILifecycleService>((name, args) =>
        {
            var callback = (Action<LifecycleEvent>)args[0]!;
            if (name == "add_Changed") Lifecycle.Add(callback);
            else { Assert.Equal("remove_Changed", name); Lifecycle.Remove(callback); }
            return null;
        });
        internal IDungeonOperationService Events => Fake<IDungeonOperationService>((name, args) =>
        {
            if (name == "GetOperations") return Seed;
            if (name == "GetTargets") return Targets;
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
                if (method == "GetInstallation") return Fake<IDungeonInstallation>((_, _) => null);
                if (method == "Attach") { AttachCalls++; return new DungeonContentResult(AttachStatus, "attach", AttachStatus == DungeonContentStatus.Attached ? Guid.NewGuid() : null); }
                Assert.Equal("Dispose", method); ProviderDisposals++; return null;
            });
        });
        internal IDungeonPanelService Panel => Fake<IDungeonPanelService>((name, args) =>
        {
            if (name == "get_Capabilities") return new DungeonPanelCapabilities(true, true, ContextualActions);
            Assert.Equal("RegisterAction", name); var key = (string)args[1]!; Assert.False(Actions.ContainsKey(key)); Actions.Add(key, (Action<DungeonPanelSnapshot>)args[3]!); Presenters.Add(key, (Func<DungeonPanelSnapshot, DungeonPanelAction?>)args[2]!);
            return new Lease(() => { Actions.Remove(key); Presenters.Remove(key); });
        });
        internal CargoAuthorSession Create(bool optional = true, bool required = true) => new("item", Life, Events, required ? Content : null,
            optional ? Panel : null, Fake<IDungeonCommandService>((_, _) => throw new InvalidOperationException()), Fake<IDungeonTacticalService>((_, _) => throw new InvalidOperationException()),
            Fake<IDungeonSettlementService>((name, args) => { var handler = (Action<DungeonSettlementSnapshot>)args[0]!; if (name == "add_Changed") { SettlementLeases++; Settlements.Add(handler); } else { Assert.Equal("remove_Changed", name); SettlementLeases--; Settlements.Remove(handler); } return null; }), Logs.Add);
        /// <summary>Gives the session an installation to adopt, as the plugin's DerelictSite would.</summary>
        internal CargoAuthorSession Owning(out BoardingHandle target, bool optional = true)
        {
            var session = Create(optional);
            var handle = new BoardingHandle(Session, Guid.NewGuid());
            Targets = new[] { new BoardingTargetSnapshot(handle, 1, BoardingEncounterKind.Installation, "Authored", null, null, BoardingAvailability.Available, null) };
            session.OwnInstallation = () => Fake<IDungeonInstallation>((_, _) => null);
            target = handle;
            return session;
        }
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
    public void VanillaTargetsAreNeverTouchedWithoutAnAuthoredInstallation()
    {
        var f = new Fixture(); using var author = f.Create();
        var a = new BoardingHandle(f.Session, Guid.NewGuid()); var b = new BoardingHandle(f.Session, Guid.NewGuid());
        f.Emit(f.Event(a, BoardingEventKind.OperationStarted));
        f.Emit(f.Event(b, BoardingEventKind.OperationStarted));
        // No attach attempt, no contextual action, no command or settlement lease on foreign targets.
        Assert.Equal(0, f.AttachCalls);
        Assert.Empty(f.Actions);
        Assert.Equal(0, f.SettlementLeases);
        Assert.Null(author.OwnedTarget);
        Assert.False(author.Attached);
        Assert.Equal(1, f.RegisterCalls);
    }

    [Fact]
    public void PreExistingVanillaOperationsNeverCreateControls()
    {
        var f = new Fixture(); f.Seed = new[] { f.Event(new(f.Session, Guid.NewGuid()), BoardingEventKind.OperationStarted).Operation! };
        using var author = f.Create();
        Assert.Empty(f.Actions); Assert.Equal(0, f.SettlementLeases); Assert.Equal(0, f.AttachCalls);
        f.Emit(LifecycleEventKind.GameplayInitialized); Assert.Equal(1, f.RegisterCalls);
    }

    [Fact]
    public void AdoptsOnlyItsOwnStationAndRetriesWhileTheTargetIsNotLiveYet()
    {
        var f = new Fixture(); using var author = f.Owning(out var target);
        // Not there yet: StaleTarget is a temporary refusal that must not log a failure or bind controls.
        f.Emit(f.Event(new(f.Session, Guid.NewGuid()), BoardingEventKind.OperationStarted));
        Assert.True(f.AttachCalls >= 1);
        Assert.False(author.Attached); Assert.Null(author.OwnedTarget); Assert.Empty(f.Actions);
        Assert.DoesNotContain(f.Logs, message => message.Contains("StaleTarget"));

        // The player arrives: the authored station adopts the layout exactly once.
        f.AttachStatus = DungeonContentStatus.Attached;
        f.Emit(f.Event(target, BoardingEventKind.OperationStarted));
        Assert.True(author.Attached);
        Assert.Equal(target, author.OwnedTarget);
        Assert.Single(f.Actions);
        Assert.Equal(1, f.SettlementLeases);

        var attachCalls = f.AttachCalls;
        f.Emit(f.Event(target, BoardingEventKind.OperationResumed));
        Assert.Equal(attachCalls, f.AttachCalls); // no re-attach once adopted
        Assert.Single(f.Actions);
    }

    [Fact]
    public void AmbiguousTargetSetSkipsOptionalControlsRatherThanGuessing()
    {
        var f = new Fixture(); using var author = f.Owning(out var target);
        f.Targets = f.Targets.Concat(new[]
        {
            new BoardingTargetSnapshot(new BoardingHandle(f.Session, Guid.NewGuid()), 1, BoardingEncounterKind.Ship, "Other", null, null, BoardingAvailability.Available, null)
        }).ToArray();
        f.AttachStatus = DungeonContentStatus.Attached;
        f.Emit(f.Event(target, BoardingEventKind.OperationStarted));
        // Content still attached, but no control is bound to a target that might not be ours.
        Assert.True(author.Attached);
        Assert.Null(author.OwnedTarget);
        Assert.Empty(f.Actions);
        Assert.Equal(0, f.SettlementLeases);
    }

    [Fact]
    public void OwnedTargetKeepsSettlementObservationUntilItsLastOperationRetires()
    {
        var f = new Fixture(); using var author = f.Owning(out var target);
        f.AttachStatus = DungeonContentStatus.Attached;
        var first = f.Event(target, BoardingEventKind.OperationStarted);
        var second = f.Event(target, BoardingEventKind.OperationStarted);
        f.Seed = new[] { first.Operation!, second.Operation! };
        f.Emit(first);
        Assert.Equal(1, f.SettlementLeases);

        // A removed host can still have living return pods: keep observing.
        f.Emit(new BoardingEvent(2, BoardingEventKind.Retired, first.Target, first.Operation));
        Assert.Equal(1, f.SettlementLeases);
        f.Seed = new[] { second.Operation! };
        f.Emit(new BoardingEvent(3, BoardingEventKind.OperationRetired, first.Target, first.Operation));
        Assert.Equal(1, f.SettlementLeases);

        f.Seed = Array.Empty<BoardingOperationSnapshot>();
        f.Emit(new BoardingEvent(4, BoardingEventKind.OperationRetired, second.Target, second.Operation));
        Assert.Equal(0, f.SettlementLeases);
        Assert.Null(author.OwnedTarget);
    }

    [Fact]
    public void SessionInvalidationDropsAdoptionSoAFreshStationCanBeAdopted()
    {
        var f = new Fixture(); using var author = f.Owning(out var target);
        f.AttachStatus = DungeonContentStatus.Attached;
        f.Emit(f.Event(target, BoardingEventKind.OperationStarted));
        Assert.True(author.Attached); Assert.Single(f.Actions);
        f.Emit(LifecycleEventKind.SessionInvalidated);
        Assert.False(author.Attached); Assert.Null(author.OwnedTarget);
        Assert.Empty(f.Actions); Assert.Equal(0, f.SettlementLeases);
        author.Dispose();
        Assert.Empty(f.Boarding); Assert.Empty(f.Lifecycle); Assert.Equal(1, f.ProviderDisposals);
    }

    [Fact]
    public void UnavailableRendererKeepsContentWithoutControls()
    { var f = new Fixture { ContextualActions = false }; using var author = f.Create(); Assert.Equal(1, f.RegisterCalls); Assert.Empty(f.Actions); }

    [Fact]
    public void AdoptionStillAttachesContentWhenPresentationIsUnavailable()
    {
        var f = new Fixture { ContextualActions = false }; using var author = f.Owning(out var target);
        f.AttachStatus = DungeonContentStatus.Attached;
        f.Emit(f.Event(target, BoardingEventKind.OperationStarted));
        Assert.True(author.Attached); Assert.Empty(f.Actions); Assert.Equal(0, f.SettlementLeases);
        Assert.Contains(f.Logs, message => message.Contains("optional contextual control"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CapabilityAbsenceNeverCreatesControls(bool required)
    { var f = new Fixture(); using var author = f.Create(optional: false, required: required); Assert.Equal(required ? 1 : 0, f.RegisterCalls); Assert.Empty(f.Actions); }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CatalogFailureRetriesOnlyOnceAtGameplayReadiness(bool recover)
    {
        var f = new Fixture { RejectDefinition = true }; using var author = f.Create(); Assert.Equal(1, f.RegisterCalls);
        f.RejectDefinition = !recover; f.Emit(LifecycleEventKind.GameplayInitialized); f.Emit(LifecycleEventKind.GameplayInitialized);
        Assert.Equal(2, f.RegisterCalls);
        Assert.Equal(recover, author.Encounter != null);
    }
}
