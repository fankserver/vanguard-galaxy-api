using System;
using System.Threading.Tasks;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldTravelAsyncCompletionTests
{
    private sealed class QueuedContext : System.Threading.SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _pending = new();
        public override void Post(System.Threading.SendOrPostCallback callback, object? state) => _pending.Enqueue(() => callback(state));
        internal void Drain() { while (_pending.TryDequeue(out var callback)) callback(); }
    }

    [Fact]
    public async Task BackgroundNativeCompletionPublishesOnlyOnCapturedGameContext()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var leg = scopes.First(route, new object());
        var pending = new TaskCompletionSource<bool>(); var context = new QueuedContext();
        int gameThread = Environment.CurrentManagedThreadId, checks = 0, writes = 0;
        Task<bool> result;
        var previous = System.Threading.SynchronizationContext.Current;
        try
        {
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            result = WorldTravelAsyncCompletion.Run(scopes, leg, () =>
            {
                Assert.Equal(gameThread, Environment.CurrentManagedThreadId);
                scopes.RequireActive(leg, session, player, manager); checks++;
            }, () => pending.Task, () => { Assert.Equal(gameThread, Environment.CurrentManagedThreadId); writes++; });
        }
        finally { System.Threading.SynchronizationContext.SetSynchronizationContext(previous); }
        var worker = new System.Threading.Thread(() => pending.SetResult(true));
        worker.Start(); worker.Join();
        Assert.False(result.IsCompleted); Assert.Equal(0, writes);
        context.Drain();
        Assert.True(result.IsCompletedSuccessfully); Assert.True(await result);
        Assert.Equal(2, checks); Assert.Equal(1, writes);
    }

    [Fact]
    public async Task LateOperationIsObservedWithoutPublishingIntoReplacementRoute()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object(); var target = new object();
        var route = scopes.Begin(session, player, manager, target); var leg = scopes.First(route, target);
        var pending = new TaskCompletionSource<bool>(); int writes = 0;
        var task = WorldTravelAsyncCompletion.Run(scopes, leg, () => scopes.RequireActive(leg, session, player, manager), () => pending.Task, () => writes++);
        var next = scopes.First(scopes.Begin(session, player, manager, target), target);
        pending.SetResult(true);
        Assert.False(await task); Assert.Equal(0, writes);
        scopes.RequireActive(next, session, player, manager);
    }
    [Fact]
    public async Task SupersededFaultPreservesNewerOperationOnSameLeg()
    {
        var scopes = new WorldTravelScopes(); var session = Guid.NewGuid(); var player = new object(); var manager = new object();
        var route = scopes.Begin(session, player, manager, new object()); var leg = scopes.First(route, new object());
        var older = new TaskCompletionSource<bool>(); var newer = new TaskCompletionSource<bool>(); int writes = 0;
        var oldTask = WorldTravelAsyncCompletion.Run(scopes, leg, () => { }, () => older.Task, () => writes++);
        var newTask = WorldTravelAsyncCompletion.Run(scopes, leg, () => { }, () => newer.Task, () => writes++);
        var failure = new InvalidOperationException("old unload failed"); older.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => oldTask));
        scopes.RequireActive(leg, session, player, manager);
        newer.SetResult(true); Assert.True(await newTask); Assert.Equal(1, writes);
    }

    [Fact]
    public async Task FinalValidationCannotReplaceTicketAndStillPublish()
    {
        var scopes = new WorldTravelScopes(); var route = scopes.Begin(Guid.NewGuid(), new object(), new object(), new object()); var leg = scopes.First(route, new object());
        int checks = 0, writes = 0; WorldTravelScopes.AsyncCompletion? replacement = null;
        bool result = await WorldTravelAsyncCompletion.Run(scopes, leg,
            () => { if (++checks == 2) replacement = scopes.BeginAsync(leg); }, () => Task.CompletedTask, () => writes++);
        Assert.False(result); Assert.Equal(0, writes); Assert.True(scopes.IsCurrent(replacement!));
    }
    [Fact]
    public async Task CurrentCompletionPublishesOnceAndNativeFailureKeepsIdentity()
    {
        var scopes = new WorldTravelScopes(); var route = scopes.Begin(Guid.NewGuid(), new object(), new object(), new object()); var leg = scopes.First(route, new object());
        int writes = 0;
        Assert.True(await WorldTravelAsyncCompletion.Run(scopes, leg, () => { }, () => Task.CompletedTask, () => writes++));
        Assert.Equal(1, writes);
        var failure = new InvalidOperationException("native unload failed");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => WorldTravelAsyncCompletion.Run(scopes, leg,
            () => { }, () => Task.FromException(failure), () => writes++)));
        Assert.Equal(1, writes);
    }
}
