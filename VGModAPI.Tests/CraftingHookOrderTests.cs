using System;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class CraftingHookOrderTests
{
    [Fact]
    public void TransferLeavesArePatchedBeforeRoutesBatchesAndOuterCallers()
    {
        int At(string key) => Array.FindIndex(CraftingJobBindings.Hooks, hook => hook.Key == key);
        foreach (var process in new[] { "Forge", "Refinery" })
        {
            Assert.True(At("jobMaterialAdd") < At("jobBatch" + process));
            Assert.True(At("jobInventoryAdd") < At("jobRoute" + process));
            Assert.True(At("jobRoute" + process) < At("jobBatch" + process));
            Assert.True(At("jobBatch" + process) < At("jobProgress" + process));
            Assert.True(At("jobMaterialAdd") < At("jobCancel" + process));
        }
    }
}
