using System;
using System.Collections.Generic;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldLoadHookInstallationTests
{
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
