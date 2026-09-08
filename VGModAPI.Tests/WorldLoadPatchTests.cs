using System;
using System.IO;
using VGModAPI.Core.Integration;
using VGModAPI.Patches;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadPatchTests : IDisposable
{
    private sealed class Host : IWorldLoadHookHost
    {
        internal bool Handled;
        internal Exception? Failure;
        internal object? Root;
        internal object? FactoryValue;
        public bool TryRecall(object file, out object? result)
        {
            if (Failure != null) throw Failure;
            result = Root; return Handled;
        }
        public void RequireFactory(object value)
        {
            FactoryValue = value;
            if (Failure != null) throw Failure;
        }
    }
    public void Dispose() => WorldLoadPatches.Host = null;

    [Fact]
    public void CapturedRootReplacesRecallRatherThanCausingAnotherRead()
    {
        var root = new object(); var host = new Host { Handled = true, Root = root }; WorldLoadPatches.Host = host;
        object? result = null;
        Assert.False(WorldLoadPatches.Recall.Prefix(new object(), ref result)); Assert.Same(root, result);
        host.Handled = false; Assert.True(WorldLoadPatches.Recall.Prefix(new object(), ref result));
    }

    [Fact]
    public void ProtectionFailuresNeverFallBackToOriginalRecallOrFactory()
    {
        var error = new InvalidDataException("unverified"); var host = new Host { Failure = error }; WorldLoadPatches.Host = host;
        object? result = null;
        Assert.Same(error, Assert.Throws<InvalidDataException>(() => WorldLoadPatches.Recall.Prefix(new object(), ref result)));
        var value = new object();
        Assert.Same(error, Assert.Throws<InvalidDataException>(() => WorldLoadPatches.Factory.Prefix(value)));
        Assert.Same(value, host.FactoryValue);
        host.Failure = null; host.Handled = true;
        Assert.Throws<InvalidOperationException>(() => WorldLoadPatches.Recall.Prefix(new object(), ref result));
    }
}
