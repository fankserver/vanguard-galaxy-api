using System;
using VGModAPI.Core;
using Xunit;
namespace VGModAPI.Tests;
public sealed class DungeonPodPrefabBindingTests
{
    private sealed class Pod { }
    private sealed class Manager
    {
        private readonly Pod boardingPodPrefab = new();
        internal Pod Expected => boardingPodPrefab;
    }
    [Fact]
    public void ResolvesPrivateNativePrefabWithoutPublicizedReferenceAssumptions()
    {
        var manager = new Manager(); var field = DungeonPodPrefabBinding.Resolve(typeof(Manager), typeof(Pod));
        Assert.True(field.IsPrivate); Assert.Same(manager.Expected, field.GetValue(manager));
    }
    [Fact]
    public void MissingOrWrongPrefabTypeFailsClosed()
    {
        Assert.Throws<MissingFieldException>(() => DungeonPodPrefabBinding.Resolve(typeof(object), typeof(Pod)));
        Assert.Throws<InvalidOperationException>(() => DungeonPodPrefabBinding.Resolve(typeof(Manager), typeof(string)));
    }
}
