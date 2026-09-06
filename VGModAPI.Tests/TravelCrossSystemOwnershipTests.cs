using System;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the per-case native ownership rules, driven through a seam that reproduces
/// Unity's destroyed-object semantics: a destroyed native object keeps a live-looking MANAGED
/// reference while its liveness check reports false ("fake null").
///
/// This is the qa-80 defect: the driver cached the TravelManager singleton in its constructor,
/// before the first case loaded its own fixture. That load destroyed the captured manager, the
/// reference survived, every managed read still worked, and the first native drive reached
/// MonoBehaviour.StartCoroutine on a destroyed behaviour, which threw ArgumentNullException from
/// inside SetRouteToPOI.
/// </summary>
public sealed class TravelCrossSystemOwnershipTests
{
    /// <summary>A native object that can be destroyed while its managed reference stays valid.</summary>
    private sealed class FakeNativeObject
    {
        internal string Name { get; }
        internal bool Destroyed { get; private set; }
        internal FakeNativeObject(string name) { Name = name; }
        internal void Destroy() => Destroyed = true;
    }

    /// <summary>The Unity-equivalent liveness rule the pilot passes in at runtime.</summary>
    private static bool Alive(object? value) => value is FakeNativeObject fake ? !fake.Destroyed : value != null;

    private static readonly Guid Session = Guid.NewGuid();
    private const string SystemId = "system-1";
    private const string StartPoi = "station-1";

    private static (NativeCaseOwner Owner, FakeNativeObject Manager, object Player) Captured(Guid? session = null)
    {
        var manager = new FakeNativeObject("TravelManager");
        var player = new object();
        return (new NativeCaseOwner(session ?? Session, manager, player, SystemId, StartPoi), manager, player);
    }

    [Fact]
    public void TheCapturedOwnerIsAcceptedWhileItIsStillTheLiveCurrentOne()
    {
        var (owner, manager, player) = Captured();
        Assert.Null(owner.CheckCurrent("a drive", Session, manager, player, Alive));
        Assert.Equal(Session, owner.Session);
        Assert.Equal(SystemId, owner.SystemId);
        Assert.Equal(StartPoi, owner.StartPoiId);
    }

    [Fact]
    public void AManagerDestroyedByALaterFixtureLoadIsRefusedInsteadOfDriven()
    {
        // Exactly the qa-80 sequence: capture, then a fixture load destroys that manager and the
        // native singleton getter hands out a fresh one.
        var (owner, manager, player) = Captured();
        manager.Destroy();
        var replacement = new FakeNativeObject("TravelManager");
        var failure = owner.CheckCurrent("native TryInitiateTravel for the in-system approach", Session, replacement, player, Alive);
        Assert.NotNull(failure);
        Assert.Contains("Refusing native TryInitiateTravel for the in-system approach", failure);
        Assert.Contains("destroyed by a later scene/fixture load", failure);
        // The trap the old logic fell into: the reference is still non-null and every managed read
        // still works, so nothing but this check stands between the probe and StartCoroutine on a
        // destroyed behaviour.
        Assert.NotNull(owner.TravelManager);
        Assert.Same(manager, owner.TravelManager);
        Assert.False(Alive(owner.TravelManager));
        Assert.True(Alive(replacement));
        // Diagnostics must show identity AND liveness of both sides plus the sessions.
        Assert.Contains("capturedManager=FakeNativeObject#", failure);
        Assert.Contains(",destroyed", failure);
        Assert.Contains("liveManager=FakeNativeObject#", failure);
        Assert.Contains(",alive", failure);
        Assert.Contains("caseSession=" + Session, failure);
    }

    [Fact]
    public void AReplacedButStillAliveManagerIsRefusedToo()
    {
        var (owner, _, player) = Captured();
        var replacement = new FakeNativeObject("TravelManager");
        var failure = owner.CheckCurrent("a drive", Session, replacement, player, Alive);
        Assert.Contains("different instance", failure);
    }

    [Fact]
    public void AMissingLiveManagerIsRefusedRatherThanDriven()
    {
        var (owner, manager, player) = Captured();
        Assert.Contains("no live native travel manager", owner.CheckCurrent("a drive", Session, null, player, Alive));
        var destroyedCurrent = manager;
        destroyedCurrent.Destroy();
        Assert.Contains("destroyed", owner.CheckCurrent("a drive", Session, destroyedCurrent, player, Alive));
    }

    [Fact]
    public void ACaseCannotAdoptAReplacementSession()
    {
        var (owner, manager, player) = Captured();
        var otherSession = Guid.NewGuid();
        var failure = owner.CheckCurrent("a drive", otherSession, manager, player, Alive);
        Assert.Contains("not the session this case was prepared in", failure);
        Assert.Contains("liveSession=" + otherSession, failure);
        // Immutable: a refusal never rebinds the owner to the replacement.
        Assert.Equal(Session, owner.Session);
        Assert.Same(manager, owner.TravelManager);
        Assert.Null(owner.CheckCurrent("a drive", Session, manager, player, Alive));
    }

    [Fact]
    public void TheCapturedPlayerMustAlsoStillBeTheLiveOne()
    {
        var (owner, manager, _) = Captured();
        Assert.Contains("live native player is a different instance", owner.CheckCurrent("a drive", Session, manager, new object(), Alive));
        Assert.Contains("live native player is a different instance", owner.CheckCurrent("a drive", Session, manager, null, Alive));
        var live = new FakeNativeObject("GamePlayer");
        var withNativePlayer = new NativeCaseOwner(Session, manager, live, SystemId, StartPoi);
        Assert.Null(withNativePlayer.CheckCurrent("a drive", Session, manager, live, Alive));
        live.Destroy();
        Assert.Contains("captured native player is gone", withNativePlayer.CheckCurrent("a drive", Session, manager, live, Alive));
    }

    [Fact]
    public void AnOwnerCannotBeCapturedWithoutASessionManagerPlayerAndLocation()
    {
        var manager = new FakeNativeObject("TravelManager");
        Assert.Throws<ArgumentException>(() => new NativeCaseOwner(Guid.Empty, manager, new object(), SystemId, StartPoi));
        Assert.Throws<ArgumentException>(() => new NativeCaseOwner(Session, manager, new object(), "", StartPoi));
        Assert.Throws<ArgumentNullException>(() => new NativeCaseOwner(Session, null!, new object(), SystemId, StartPoi));
        Assert.Throws<ArgumentNullException>(() => new NativeCaseOwner(Session, manager, null!, SystemId, StartPoi));
        var (owner, live, player) = Captured();
        Assert.Throws<ArgumentNullException>(() => owner.CheckCurrent("a drive", Session, live, player, null!));
        Assert.Equal("<null>", NativeCaseOwner.Identity(null, Alive));
    }

    [Fact]
    public void CrossSystemEvidenceRejectsFactsObservedOutsideTheCapturedOwner()
    {
        // The observation side of the same rule: a fact sampled while the live manager/player is no
        // longer the captured one is not this case's evidence.
        var operation = Guid.NewGuid();
        var origin = new TravelLocation(SystemId, StartPoi, SystemId, StartPoi);
        var requested = new TravelLocation("system-2", "gate-2", "system-2", "gate-2");
        var facts = new[]
        {
            new TravelTransition(Session, operation, 1, TravelTransitionKind.Requested, TravelMode.JumpGate, null, requested, null, 1, null),
            new TravelTransition(Session, operation, 2, TravelTransitionKind.Departed, TravelMode.JumpGate, origin, null, null, 2, null),
            new TravelTransition(Session, operation, 3, TravelTransitionKind.Arrived, TravelMode.JumpGate, origin, requested, requested, 3, null)
        };
        TravelCrossSystemReceipt.NativeSnapshot Snapshot(bool owned)
            => new(true, true, TravelCrossSystemReceipt.ManagerTypeFor(TravelMode.JumpGate),
                TravelStationReceipt.Location("system-2", "gate-2"), true, owned);
        var owned = new System.Collections.Generic.Dictionary<long, TravelCrossSystemReceipt.NativeSnapshot>
            { [1] = Snapshot(true), [2] = Snapshot(true), [3] = Snapshot(true) };
        Assert.Null(TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, owned, TravelMode.JumpGate));
        foreach (long sequence in new long[] { 2, 3 })
        {
            var foreign = new System.Collections.Generic.Dictionary<long, TravelCrossSystemReceipt.NativeSnapshot>(owned)
                { [sequence] = Snapshot(false) };
            Assert.Contains("not the instance this case captured",
                TravelCrossSystemReceipt.CheckJumpIteratorEvidence(facts, foreign, TravelMode.JumpGate));
        }
        Assert.Contains(",owned=False", Snapshot(false).ToDetail());
    }
}
