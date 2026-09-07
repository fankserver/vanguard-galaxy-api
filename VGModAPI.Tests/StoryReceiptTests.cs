using System;
using System.Linq;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryReceiptTests
{
    public sealed class Mission { }
    public sealed class Player
    {
        public bool Claimed;
        public void CompleteMission(string name) => throw new InvalidOperationException("Wrong overload");
        public void CompleteMission(Mission mission, bool force) { Assert.False(force); Claimed = true; }
    }

    [Fact]
    public void NativeClaimUsesMissionAndBooleanOverload()
    {
        var player = new Player();
        StoryNativeCalls.CompleteMission(typeof(Player), typeof(Mission)).Invoke(player, new object[] { new Mission(), false });
        Assert.True(player.Claimed);
        Assert.Throws<MissingMethodException>(() => StoryNativeCalls.CompleteMission(typeof(Player), typeof(object)));
    }

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
