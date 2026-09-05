using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BindingOrderTests
{
    [Fact]
    public void LoadIteratorFactoryIsPatchedBeforeTheCallingMethod()
    {
        Assert.True(Index(BindingCatalog.Session, "loadRoutine") < Index(BindingCatalog.Session, "load"));
    }

    [Theory]
    [InlineData("writeFile")]
    [InlineData("writeMetadata")]
    [InlineData("storeFailure")]
    public void SaveEvidenceHooksAreInstalledBeforeStore(string callee)
    {
        Assert.True(Index(BindingCatalog.Saves, callee) < Index(BindingCatalog.Saves, "store"));
    }

    private static int Index(MethodBinding[] catalog, string key)
    {
        int result = Array.FindIndex(catalog, b => b.Key == key);
        Assert.True(result >= 0, "Missing binding " + key);
        return result;
    }
}
