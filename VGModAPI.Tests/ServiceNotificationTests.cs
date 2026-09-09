using System;
using System.Collections.Generic;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ServiceNotificationTests
{
    internal static Exception? OnWorker(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(action));
        thread.Start(); thread.Join();
        return error;
    }

    [Fact]
    public void NoReplayAndIndependentMulticastHandlersEvenWhenLoggingThrows()
    {
        using var hub = new LifecycleHub((_, _) => { });
        var observed = new List<int>();
        var reports = 0;
        var dispatch = new List<bool>();
        using var events = new ServiceNotifications<int>(hub.CheckThread, (_, _) => { reports++; throw new Exception(); }, hub.EnterServiceDispatch);
        Action<int> combined = _ => { dispatch.Add(hub.IsDispatchingCallbacks); throw new Exception(); };
        combined += value => observed.Add(value);
        events.Add(combined);
        Assert.Empty(observed);
        events.Publish(1); events.Publish(2);
        Assert.Equal(new[] { 1, 2 }, observed);
        Assert.Equal(2, reports);
        Assert.Equal(new[] { true, true }, dispatch);
        Assert.False(hub.IsDispatchingCallbacks);
    }

    [Fact]
    public void RemovalSuppressesCurrentTurnAndAdditionsJoinTheNextDispatch()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new ServiceNotifications<int>(hub.CheckThread, (_, _) => { }, hub.EnterServiceDispatch);
        var seen = new List<string>();
        Action<int> removed = n => seen.Add("removed" + n);
        Action<int> added = n => seen.Add("added" + n);
        events.Add(n =>
        {
            seen.Add("first" + n);
            if (n != 1) return;
            events.Remove(removed);
            events.Publish(2);
            events.Add(added);
        });
        events.Add(removed);
        events.Publish(1);
        Assert.Equal(new[] { "first1", "first2", "added2" }, seen);
    }

    [Fact]
    public void MulticastRemovalMatchesTheLastContiguousSequence()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new ServiceNotifications<int>(hub.CheckThread, (_, _) => { }, hub.EnterServiceDispatch);
        var seen = new List<string>();
        Action<int> a = _ => seen.Add("a"), b = _ => seen.Add("b"), c = _ => seen.Add("c");
        var pair = a + b;
        events.Add(a); events.Add(c); events.Add(b);
        events.Remove(pair);
        events.Publish(0);
        Assert.Equal(new[] { "a", "c", "b" }, seen);
        seen.Clear();
        events.Add(pair); events.Add(b); events.Remove(pair);
        events.Publish(0);
        Assert.Equal(new[] { "a", "c", "b", "b" }, seen);
    }

    [Fact]
    public void DisposalDuringCallbackClearsPendingAndRemainingCallbacks()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new ServiceNotifications<int>(hub.CheckThread, (_, _) => { }, hub.EnterServiceDispatch);
        var calls = 0;
        events.Add(_ => { calls++; events.Publish(2); events.Dispose(); });
        events.Add(_ => calls++);
        events.Publish(1);
        Assert.Equal(1, calls);
        Assert.False(hub.IsDispatchingCallbacks);
        events.Remove(null); events.Dispose(); events.Publish(3);
        Assert.Throws<ObjectDisposedException>(() => events.Add(_ => { }));
    }

    [Fact]
    public void WrongThreadCannotRegisterPublishOrDispose()
    {
        using var hub = new LifecycleHub((_, _) => { });
        using var events = new ServiceNotifications<int>(hub.CheckThread, (_, _) => { }, hub.EnterServiceDispatch);
        foreach (Action action in new Action[] { () => events.Add(_ => { }), () => events.Publish(1), events.Dispose, () => events.Remove(null) })
        {
            var error = OnWorker(action);
            Assert.IsType<InvalidOperationException>(error);
        }
    }
}
