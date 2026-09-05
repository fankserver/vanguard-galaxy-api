using System;
using System.Collections.Generic;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public class DispatchStateTests
{
    [Fact]
    public void StateCoversReentrantQueueAndDiagnosticsAndResetsAfterDrain()
    {
        LifecycleHub? hub = null;
        var observations = new List<bool>();
        hub = new LifecycleHub((_, _) => observations.Add(hub!.IsDispatchingCallbacks));
        using (hub)
        {
            ILifecycleDispatchState state = hub;
            Assert.False(state.IsDispatchingCallbacks);
            using var first = hub.Subscribe("first", e =>
            {
                observations.Add(state.IsDispatchingCallbacks);
                if (e.Kind == LifecycleEventKind.SessionStarting)
                {
                    hub.PlayerReady(e.Session!.Id);
                    observations.Add(state.IsDispatchingCallbacks);
                    throw new InvalidOperationException("expected");
                }
            });
            using var second = hub.Subscribe("second", _ => observations.Add(state.IsDispatchingCallbacks));
            hub.Begin(SessionOrigin.NewGame, null);
            Assert.Equal(6, observations.Count);
            Assert.All(observations, Assert.True);
            Assert.False(state.IsDispatchingCallbacks);
        }
    }

    [Fact]
    public void DisposalWithinCallbackDoesNotEndDispatchEarly()
    {
        using var hub = new LifecycleHub((_, _) => { });
        bool? inside = null;
        using var subscription = hub.Subscribe("dispose", _ =>
        {
            hub.Dispose();
            inside = hub.IsDispatchingCallbacks;
        });
        hub.Begin(SessionOrigin.NewGame, null);
        Assert.True(inside);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void ForeignThreadCannotQueryDispatchState()
    {
        using var hub = new LifecycleHub((_, _) => { });
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(() => _ = hub.IsDispatchingCallbacks));
        thread.Start();
        thread.Join();
        Assert.IsType<InvalidOperationException>(error);
    }
}
