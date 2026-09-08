using System;
using System.Collections.Generic;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadHookInstallationTests
{
    [Fact]
    public void RepeatedRollbackFailureRetainsRefusalAndDoesNotEscapeCleanup()
    {
        bool stopped = false, retained = false, continued = false;
        int rollbacks = 0;
        void Rollback() { rollbacks++; throw new InvalidOperationException("rollback failed"); }
        try
        {
            WorldLoadHookInstallation.Install(() => { }, () => throw new InvalidOperationException("patch failed"), Rollback);
        }
        catch (Exception)
        {
            // Same cleanup path used by Plugin.InitializeWorldProtection.
            WorldLoadHookInstallation.CleanupFailure(() => stopped = true, Rollback,
                () => { Assert.True(stopped); retained = true; }, _ => throw new Exception("logger failed"));
            continued = true;
        }
        Assert.Equal(2, rollbacks);
        Assert.True(stopped); Assert.True(retained); Assert.True(continued);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FactoryPrecedesReaderAndPartialFailureRollsBack(int failAt)
    {
        var calls = new List<string>();
        var failure = new InvalidOperationException("injected");
        void Factory() { calls.Add("factory"); if (failAt == 1) throw failure; }
        void Recall() { calls.Add("recall"); if (failAt == 2) throw failure; }
        void Rollback() => calls.Add("rollback");
        if (failAt == 0)
        {
            WorldLoadHookInstallation.Install(Factory, Recall, Rollback);
            Assert.Equal(new[] { "factory", "recall" }, calls);
        }
        else
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => WorldLoadHookInstallation.Install(Factory, Recall, Rollback)));
            Assert.Equal(failAt == 1 ? new[] { "factory", "rollback" } : new[] { "factory", "recall", "rollback" }, calls);
        }
    }
}
