using System;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ForgeReadRegistryTests
{
    [Fact]
    public void ParentAndVariantsShareCompleteIdentityDeduplicatedBaseline()
    {
        var parent = new EqualValue(); var child = new EqualValue(); var sibling = new EqualValue();
        var result = ForgeReadRegistry.Expand(new object[] { parent, parent },
            _ => new object[] { parent, child, child, sibling });
        Assert.Equal(3, result.Length);
        Assert.Same(parent, result[0]); Assert.Same(child, result[1]); Assert.Same(sibling, result[2]);
    }
    private sealed class EqualValue
    {
        public override bool Equals(object? obj) => obj is EqualValue;
        public override int GetHashCode() => 1;
    }
}
