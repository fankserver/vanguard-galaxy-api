using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Source.Player;
using Source.Util;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class GameAdapterTests
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly List<LifecycleEvent> _events = new();
    private readonly GameAdapter _adapter;
    public GameAdapterTests()
    {
        GamePlayer.current = null;
        _adapter = new GameAdapter(_hub, new GameBindings(typeof(GamePlayer).Assembly), _ => { });
        _hub.Subscribe("test", _events.Add);
    }
    private static SaveGameFile File(string name = "test.save") => new(Path.Combine(Path.GetTempPath(), name));
    private static void Drain(IEnumerator routine)
    {
        while (routine.MoveNext()) if (routine.Current is IEnumerator child) Drain(child);
    }

    [Fact]
    public void LoadMethodReturnDoesNotImplyReadiness()
    {
        var request = _adapter.BeginLoad(File());
        IEnumerator Root() { yield return null; GamePlayer.current = new GamePlayer(); _adapter.PlayerReconstructed(); }
        var routine = _adapter.ObserveLoad(Root());
        _adapter.EndLoadRequest(request, null);
        Assert.Equal(SessionPhase.Starting, _hub.CurrentSession!.Phase);
        Drain(routine);
        Assert.Equal(SessionPhase.PlayerReady, _hub.CurrentSession.Phase);
        Assert.NotNull(_adapter.CaptureGameplay());
        _adapter.GameplayCompleted(request.Id, new GameplayManager(true), null);
        Assert.Equal(SessionPhase.GameplayInitialized, _hub.CurrentSession.Phase);
    }

    [Fact]
    public void MissingIteratorHookFailsInsteadOfRemainingStarting()
    {
        var request = _adapter.BeginLoad(File());
        _adapter.EndLoadRequest(request, null);
        Assert.Equal(SessionPhase.Failed, _hub.CurrentSession!.Phase);
        Assert.Contains("not observed", Assert.Single(_events, e => e.Kind == LifecycleEventKind.SessionStartFailed).Detail);
    }

    [Fact]
    public void NestedRequestsRestoreTheirOwnObservationFlags()
    {
        var outer = _adapter.BeginLoad(File("outer.save"));
        var inner = _adapter.BeginLoad(File("inner.save"));
        IEnumerator Root() { yield return null; }
        _adapter.ObserveLoad(Root());
        _adapter.EndLoadRequest(inner, null);
        _adapter.ObserveLoad(Root());
        _adapter.EndLoadRequest(outer, null);
        Assert.True(outer.Observed);
        Assert.True(inner.Observed);
        Assert.Equal(SessionPhase.Starting, _hub.CurrentSession!.Phase);
    }

    [Fact]
    public void NestedLoadMaintainsAttemptContextAfterRequestReturned()
    {
        var request = _adapter.BeginLoad(File());
        IEnumerator Inner() { yield return null; GamePlayer.current = new GamePlayer(); _adapter.PlayerReconstructed(); }
        IEnumerator Root() { yield return Inner(); }
        var routine = _adapter.ObserveLoad(Root()); _adapter.EndLoadRequest(request, null); Drain(routine);
        Assert.Equal(SessionPhase.PlayerReady, _hub.CurrentSession!.Phase);
    }

    [Fact]
    public void RejectedVersionEndingWithoutPlayerReportsFailure()
    {
        var request = _adapter.BeginLoad(File());
        IEnumerator Rejected() { yield break; }
        var routine = _adapter.ObserveLoad(Rejected()); _adapter.EndLoadRequest(request, null); Drain(routine);
        Assert.Equal(SessionPhase.Failed, _hub.CurrentSession!.Phase);
        Assert.Single(_events, e => e.Kind == LifecycleEventKind.SessionStartFailed);
    }

    [Fact]
    public void VanillaSwallowedFailureStillReportsFailure()
    {
        var request = _adapter.BeginLoad(File());
        IEnumerator Failed() { yield return null; _adapter.LoadFailed(); }
        var routine = _adapter.ObserveLoad(Failed()); _adapter.EndLoadRequest(request, null); Drain(routine);
        Assert.Single(_events, e => e.Kind == LifecycleEventKind.SessionStartFailed);
    }

    [Fact]
    public void OldCoroutineCannotReadyOrFailReplacementAttempt()
    {
        var a = _adapter.BeginLoad(File("a.save"));
        IEnumerator Old() { yield return null; _adapter.PlayerReconstructed(); _adapter.LoadFailed(); }
        var old = _adapter.ObserveLoad(Old()); _adapter.EndLoadRequest(a, null);
        var b = _adapter.BeginLoad(File("b.save"));
        _adapter.ObserveLoad(Old()); _adapter.EndLoadRequest(b, null);
        GamePlayer.current = new GamePlayer(); Drain(old);
        Assert.Equal(b.Id, _hub.CurrentSession!.Id); Assert.Equal(SessionPhase.Starting, _hub.CurrentSession.Phase);
    }

    [Fact]
    public void MenuRejectsLateLoadReadiness()
    {
        var a = _adapter.BeginLoad(File());
        IEnumerator Old() { yield return null; GamePlayer.current = new GamePlayer(); _adapter.PlayerReconstructed(); }
        var old = _adapter.ObserveLoad(Old()); _adapter.EndLoadRequest(a, null);
        _adapter.Invalidate("menu"); Drain(old);
        Assert.Equal(SessionPhase.Invalidated, _hub.CurrentSession!.Phase);
    }

    [Fact]
    public void NewGameWaitsForSceneBoundaryNotCreation()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer(); _adapter.EndNewPlayer(id, null); _adapter.Poll();
        Assert.Equal(SessionPhase.Starting, _hub.CurrentSession!.Phase);
        _adapter.PlayerReconstructed(); _adapter.GameplayCompleted(id, new GameplayManager(false), null);
        Assert.Equal(SessionPhase.PlayerReady, _hub.CurrentSession.Phase);
        _adapter.GameplayCompleted(id, new GameplayManager(true), null);
        Assert.Equal(SessionPhase.GameplayInitialized, _hub.CurrentSession.Phase);
    }

    [Fact]
    public void UntrackedReplacementInvalidatesRatherThanInventingReadiness()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer(); _adapter.EndNewPlayer(id, null); _adapter.PlayerReconstructed();
        GamePlayer.current = new GamePlayer(); _adapter.Poll(); _adapter.Poll();
        Assert.Equal(SessionPhase.Invalidated, _hub.CurrentSession!.Phase);
        Assert.Single(_events, e => e.Kind == LifecycleEventKind.SessionInvalidated);
        Assert.Null(_adapter.SaveSession());
    }

    [Fact]
    public void SaveSessionIsUnknownWhileAnotherSaveIsLoading()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer(); _adapter.EndNewPlayer(id, null); _adapter.PlayerReconstructed();
        Assert.NotNull(_adapter.SaveSession());
        _adapter.BeginLoad(File()); Assert.Null(_adapter.SaveSession());
    }

    [Fact]
    public void PendingNewGameCannotAdoptAnUntrackedReplacement()
    {
        var id = _adapter.BeginNewPlayer();
        GamePlayer.current = new GamePlayer();
        _adapter.EndNewPlayer(id, null);
        // Another path replaces its player before scenes are requested.
        GamePlayer.current = new GamePlayer();
        Assert.Null(_adapter.PlayerReconstructed());
        Assert.Equal(SessionPhase.Invalidated, _hub.CurrentSession!.Phase);
        Assert.DoesNotContain(_events, e => e.Kind == LifecycleEventKind.PlayerReady);
    }

    [Fact]
    public void CreationMustCompleteBeforeNewGameCanBecomeReady()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer();
        Assert.Null(_adapter.PlayerReconstructed());
        _adapter.EndNewPlayer(id, null);
        Assert.Equal(SessionPhase.Starting, _hub.CurrentSession!.Phase);
        Assert.Equal(id, _adapter.PlayerReconstructed());
    }

    [Fact]
    public void StaleCreationCompletionCannotCaptureOrFailReplacement()
    {
        var old = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer();
        var next = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer();
        _adapter.EndNewPlayer(next, null);
        _adapter.EndNewPlayer(old, new Exception());
        Assert.Equal(next, _adapter.PlayerReconstructed());
    }

    [Fact]
    public void PendingPlayerReplacementIsDetectedByPoll()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer(); _adapter.EndNewPlayer(id, null);
        GamePlayer.current = null; _adapter.Poll();
        Assert.Equal(SessionPhase.Invalidated, _hub.CurrentSession!.Phase);
        Assert.Null(_adapter.SaveSession());
    }

    [Fact]
    public void UntrackedIteratorIsNotWrappedOrAssignedAReadySession()
    {
        IEnumerator Idle() { yield return null; }
        var routine = Idle();
        Assert.Same(routine, _adapter.ObserveLoad(routine));
        GamePlayer.current = new GamePlayer();
        Assert.Null(_adapter.PlayerReconstructed());
        Assert.Null(_hub.CurrentSession);
    }

    [Fact]
    public void SilentlyAbandonedIteratorDoesNotInventATerminalSignal()
    {
        IEnumerator Idle() { yield return null; }
        var request = _adapter.BeginLoad(File());
        _adapter.ObserveLoad(Idle());
        _adapter.EndLoadRequest(request, null);
        _adapter.Poll();
        Assert.Equal(SessionPhase.Starting, _hub.CurrentSession!.Phase);
        Assert.Single(_events);
    }

    [Fact]
    public void MissingNewPlayerFailsWithoutReadiness()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = null;
        _adapter.EndNewPlayer(id, null);
        Assert.Equal(SessionPhase.Failed, _hub.CurrentSession!.Phase);
        Assert.Null(_adapter.PlayerReconstructed());
    }

    [Fact]
    public void MidGameAttachDoesNotGuessReadiness()
    {
        GamePlayer.current = new GamePlayer(); _adapter.Poll();
        Assert.Null(_adapter.CaptureGameplay()); Assert.Null(_adapter.PlayerReconstructed()); Assert.Null(_hub.CurrentSession);
    }

    [Fact]
    public void GameplayFailureIsNotInitializationSuccess()
    {
        var id = _adapter.BeginNewPlayer(); GamePlayer.current = new GamePlayer(); _adapter.EndNewPlayer(id, null); _adapter.PlayerReconstructed();
        _adapter.GameplayCompleted(id, new GameplayManager(true), new Exception());
        Assert.Equal(SessionPhase.Failed, _hub.CurrentSession!.Phase);
    }

    [Fact]
    public void ObserverFaultIsContainedAndDisablesFutureObservations()
    {
        _adapter.BeginNewPlayer();
        _adapter.Guard(() => throw new InvalidOperationException("fault"));
        bool invoked = false; _adapter.Guard(() => invoked = true); _adapter.Poll();
        Assert.False(invoked); Assert.All(_hub.Capabilities, c => Assert.False(c.Available));
        Assert.Equal(SessionPhase.Invalidated, _hub.CurrentSession!.Phase);
    }
}
