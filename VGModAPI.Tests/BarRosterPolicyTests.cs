using System;
using System.Linq;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarRosterPolicyTests
{
    [Fact]
    public void AdditiveProvidersKeepVanillaAndHaveDeterministicOrder()
    {
        var claims = new[] { new BarRosterPolicy.Claim("job", false, false), new BarRosterPolicy.Claim("campaign", false, false) };
        var result = BarRosterPolicy.Resolve(claims);
        Assert.True(result.KeepVanilla);
        Assert.Equal(new[] { "campaign", "job" }, result.Admitted);
        Assert.Equal(result.Admitted, BarRosterPolicy.Resolve(claims.Reverse().ToArray()).Admitted);
        Assert.Empty(result.Denied);
    }

    [Fact]
    public void AuthorizedExclusiveOwnerDiagnosesDeniedContributions()
    {
        var result = BarRosterPolicy.Resolve(new[] { new BarRosterPolicy.Claim("campaign", true, true), new BarRosterPolicy.Claim("job", false, false) });
        Assert.False(result.KeepVanilla);
        Assert.Equal(new[] { "campaign" }, result.Admitted);
        Assert.Equal(BarRosterPolicy.Denial.StationOwnedExclusively, result.Denied["job"]);
    }

    [Fact]
    public void MissingPermissionCannotRemoveVanillaOrOtherProviders()
    {
        var result = BarRosterPolicy.Resolve(new[] { new BarRosterPolicy.Claim("campaign", true, false), new BarRosterPolicy.Claim("job", false, false) });
        Assert.True(result.KeepVanilla);
        Assert.Equal(new[] { "job" }, result.Admitted);
        Assert.Equal(BarRosterPolicy.Denial.ExclusivePermissionRequired, result.Denied["campaign"]);
    }

    [Fact]
    public void ConflictingExclusiveClaimsNeverChooseLastWriter()
    {
        var claims = new[] { new BarRosterPolicy.Claim("a", true, true), new BarRosterPolicy.Claim("b", true, true), new BarRosterPolicy.Claim("c", false, false) };
        foreach (var ordered in new[] { claims, claims.Reverse().ToArray() })
        {
            var result = BarRosterPolicy.Resolve(ordered);
            Assert.True(result.KeepVanilla);
            Assert.Empty(result.Admitted);
            Assert.Equal(3, result.Denied.Count);
            Assert.All(result.Denied.Values, denial => Assert.Equal(BarRosterPolicy.Denial.ConflictingExclusiveClaims, denial));
        }
    }

    [Fact]
    public void DuplicateClaimsAreRefusedRatherThanMergedByOrder()
    {
        Assert.Throws<ArgumentException>(() => BarRosterPolicy.Resolve(new[] { new BarRosterPolicy.Claim("a", false, false), new BarRosterPolicy.Claim("a", true, true) }));
    }
}
