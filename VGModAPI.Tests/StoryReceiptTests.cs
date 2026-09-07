using System;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryReceiptTests
{
    [Fact]
    public void RequiresEveryCaseExactlyOnceInOrder()
    {
        Assert.Null(StoryReceipt.Evaluate(StoryReceipt.RequiredCases));
        Assert.NotNull(StoryReceipt.Evaluate(Array.Empty<string>()));
        for (int index = 0; index < StoryReceipt.RequiredCases.Length; index++)
        {
            Assert.NotNull(StoryReceipt.Evaluate(StoryReceipt.RequiredCases.Where((_, i) => i != index).ToArray()));
            var corrupted = StoryReceipt.RequiredCases.ToArray();
            corrupted[index] = "unknown-case";
            Assert.NotNull(StoryReceipt.Evaluate(corrupted));
        }
        Assert.NotNull(StoryReceipt.Evaluate(StoryReceipt.RequiredCases.Reverse().ToArray()));
        Assert.NotNull(StoryReceipt.Evaluate(StoryReceipt.RequiredCases.Concat(new[] { StoryReceipt.RequiredCases[0] }).ToArray()));
    }
}
