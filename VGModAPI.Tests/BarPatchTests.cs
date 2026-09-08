using System;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPatchTests : IDisposable
{
    private sealed class Host : IBarHookHost, IBarRefreshScope
    {
        internal int Completed, Clicks;
        internal bool Ran, Succeeded, Throw;
        internal object? Result;
        public IBarRefreshScope BeginRefresh(object bar) => this;
        public void Complete(bool originalRan, bool succeeded) { Completed++; Ran = originalRan; Succeeded = succeeded; }
        public bool TrySerialize(object bar, out object? result)
        { result = Result; if (Throw) throw new InvalidOperationException("unsafe"); return result != null; }
        public bool IsOwned(object patron) => patron is string;
        public void Interact(object patron) { Clicks++; throw new InvalidOperationException("provider failure"); }
        public void Fault(Exception error) { throw new InvalidOperationException("logger failure"); }
    }
    public void Dispose() => BarPatches.Host = null;

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RefreshFinalizerClosesCapturedHostOnceAndPreservesCompletionEvidence(bool ran, bool succeeded)
    {
        var host = new Host(); BarPatches.Host = host;
        BarPatches.Refresh.Prefix(new object(), out var state);
        BarPatches.Host = new Host();
        var error = succeeded ? null : new Exception("native failure");
        BarPatches.Refresh.Finalizer(ran, error, state);
        BarPatches.Refresh.Finalizer(ran, error, state);
        Assert.Equal(1, host.Completed);
        Assert.Equal(ran, host.Ran); Assert.Equal(succeeded, host.Succeeded);
    }

    [Fact]
    public void OwnedInteractionNeverFallsThroughToNativeSalesEvenWhenCallbackAndLoggerThrow()
    {
        var host = new Host(); BarPatches.Host = host;
        Assert.False(BarPatches.Interact.Prefix("owned"));
        Assert.Equal(1, host.Clicks);
        Assert.True(BarPatches.Interact.Prefix(new object()));
        Assert.Throws<InvalidOperationException>(() => BarPatches.PatronSerialize.Prefix("owned"));
    }

    [Fact]
    public void SerializationPrefixReturnsTheBoxedValueWithoutRunningOriginal()
    {
        BarPatches.Host = new Host { Result = new BarNativeSerializationTests.Value("managed snapshot") };
        object? result = null;
        Assert.False(BarPatches.Serialize.Prefix(new object(), ref result));
        Assert.Equal("managed snapshot", Assert.IsType<BarNativeSerializationTests.Value>(result).Data);
    }

    [Fact]
    public void UnsafeSerializationPropagatesRefusalRatherThanFallingBackToVanilla()
    {
        BarPatches.Host = new Host { Throw = true };
        object? result = null;
        Assert.Throws<InvalidOperationException>(() => BarPatches.Serialize.Prefix(new object(), ref result));
        Assert.Null(result);
    }
}
