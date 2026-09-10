using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// API-side guarantee replacing deleted consumer-side coverage: a gameplay reaction is never
/// delivered inside observational callback dispatch, only at the safe boundary afterwards.
/// </summary>
public sealed class GameplayNotificationsTests : IDisposable
{
    private readonly LifecycleHub _hub = new((_, _) => { });
    private Guid Ready()
    {
        _hub.SetCapability("session-lifecycle", true, "Bound.");
        _hub.SetCapability("save-outcomes", true, "Bound.");
        var session = _hub.Begin(SessionOrigin.SaveLoad, "slot");
        _hub.PlayerReady(session); _hub.GameplayInitialized(session);
        return session;
    }

    [Fact]
    public void ReactionsAreNeverDeliveredInsideObservationalDispatch()
    {
        var session = Ready();
        int delivered = 0;
        _hub.Gameplay.Enqueue(session, "owner", () => delivered++, () => true);
        bool sawDeliveryInsideDispatch = false;
        using var observer = _hub.Subscribe("observer", _ =>
        {
            // A consumer forcing the drain from inside an observational callback gets nothing.
            _hub.Gameplay.Tick();
            sawDeliveryInsideDispatch |= delivered > 0;
        });
        var operation = Guid.NewGuid();
        _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveStarted, _hub.CurrentSession, operation, "slot"));
        Assert.False(sawDeliveryInsideDispatch);
        Assert.Equal(0, delivered); // The in-flight save also holds delivery after dispatch ends.
        _hub.Publish(new LifecycleEvent(LifecycleEventKind.SaveSkipped, _hub.CurrentSession, operation, "slot"));
        _hub.Gameplay.Tick();
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void ReactionsAreNeverDeliveredInsideServiceDispatchScopes()
    {
        var session = Ready();
        int delivered = 0;
        _hub.Gameplay.Enqueue(session, "owner", () => delivered++, () => true);
        using (_hub.EnterServiceDispatch())
        {
            _hub.Gameplay.Tick();
            Assert.Equal(0, delivered);
        }
        _hub.Gameplay.Tick();
        Assert.Equal(1, delivered);
    }

    public void Dispose() => _hub.Dispose();
}
