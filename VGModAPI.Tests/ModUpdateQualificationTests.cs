using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModUpdateQualificationTests
{
    [Fact]
    public async Task ControlledDriverRunsTheProductionCoreAndReportsEveryPhase()
    {
        var root = Path.Combine(Path.GetTempPath(), "vg-updates-" + Guid.NewGuid().ToString("N"));
        var steps = new List<string>();
        try
        {
            await ModUpdateChecks.ControlledAsync(root, steps.Add);
            Assert.Equal(7, steps.Count);
            Assert.Contains("controlled-quit-mid-check", steps);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
