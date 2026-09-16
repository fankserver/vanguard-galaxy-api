using System;
using System.Threading;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PickupPresentationScopeTests
{
    private static ItemPickupPresentation Item(string name) => new(name, name, 1, null);

    [Fact]
    public void OnlyMatchingPickupConsumesContextAndOnlyOnce()
    {
        var scope = new PickupPresentationScope();
        using var frame = scope.Begin()!;
        var pickup = frame.Pickup = Item("Iron");
        Assert.Null(scope.Consume(false, "Iron"));
        Assert.Null(scope.Consume(true, "@UICredits"));
        Assert.Same(pickup, scope.Consume(true, "Iron"));
        Assert.Null(scope.Consume(true, "Iron"));
    }

    [Fact]
    public void NestedUnreadableFrameCannotInheritAndExceptionUnwindRestoresOuter()
    {
        var scope = new PickupPresentationScope();
        using var outer = scope.Begin()!;
        var pickup = outer.Pickup = Item("Outer");
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var unreadable = scope.Begin()!;
            Assert.Null(scope.Consume(true, "Outer"));
            using var inner = scope.Begin()!;
            inner.Pickup = Item("Inner");
            Assert.Equal("Inner", scope.Consume(true, "Inner")!.ItemId);
            throw new InvalidOperationException();
        }));
        Assert.Same(pickup, scope.Consume(true, "Outer"));
        outer.Dispose();
        Assert.Null(scope.Consume(true, "Outer"));
    }

    [Fact]
    public void WorkerThreadCannotPushConsumeOrPopMainThreadFrame()
    {
        var scope = new PickupPresentationScope();
        using var frame = scope.Begin()!;
        var pickup = frame.Pickup = Item("Iron");
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Assert.False(scope.IsOwnerThread);
                Assert.Null(scope.Begin());
                Assert.Null(scope.Consume(true, "Iron"));
                frame.Dispose();
            }
            catch (Exception error) { failure = error; }
        });
        worker.Start();
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(failure);
        Assert.Same(pickup, scope.Consume(true, "Iron"));
    }
}
