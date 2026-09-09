using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class GameServiceTests
{
    [Fact]
    public void StartedCarriesAnActionableGameAndCapturedNavigationCannotActInReplacementSave()
    {
        using var hub = new LifecycleHub((_, _) => { });
        foreach (var capability in new[] { "session-lifecycle", "save-outcomes", "navigation" }) hub.SetCapability(capability, true, "Bound");
        var focused = new List<string>();
        var navigation = new NavigationService(hub, _ => null, (_, id, current) =>
        {
            Assert.False(hub.IsDispatchingCallbacks);
            Assert.True(current()); focused.Add(id); return NavigationStatus.Succeeded;
        }, (_, _) => false);
        using var games = new GameService(hub, navigation);
        var observed = new List<IGame>();
        games.Started += game => { observed.Add(game); Assert.Equal(NavigationStatus.Succeeded, game.Navigation.FocusPoi("station")); };
        Assert.Null(games.Current);
        var firstId = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(firstId); hub.GameplayInitialized(firstId);
        Assert.Empty(observed); hub.Gameplay.Tick();
        var first = Assert.Single(observed); Assert.Same(first, games.Current); Assert.True(first.IsActive);
        var secondId = hub.Begin(SessionOrigin.NewGame, null);
        Assert.False(first.IsActive); Assert.Null(games.Current);
        hub.PlayerReady(secondId); hub.GameplayInitialized(secondId); hub.Gameplay.Tick();
        Assert.Equal(2, observed.Count); Assert.NotSame(first, games.Current);
        Assert.Equal(NavigationStatus.NotReady, first.Navigation.FocusPoi("old-work"));
        Assert.Equal(new[] { "station", "station" }, focused);
        hub.Dispose(); Assert.False(observed[1].IsActive); Assert.Null(games.Current);
    }

    [Fact]
    public void StartingSubscriptionRemovalSuppressesPendingCallback()
    {
        using var hub = new LifecycleHub((_, _) => { });
        hub.SetCapability("session-lifecycle", true, "Bound"); hub.SetCapability("save-outcomes", true, "Bound");
        using var games = new GameService(hub, new NavigationService(hub, _ => null, (_, _, _) => NavigationStatus.Unavailable, (_, _) => null));
        Action<IGame> handler = _ => Assert.Fail("Removed"); games.Started += handler;
        var id = hub.Begin(SessionOrigin.NewGame, null); hub.PlayerReady(id); hub.GameplayInitialized(id);
        games.Started -= handler; hub.Gameplay.Tick();
    }
}
