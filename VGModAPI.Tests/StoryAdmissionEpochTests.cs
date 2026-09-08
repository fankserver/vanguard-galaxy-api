using System;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryAdmissionEpochTests
{
    [Fact]
    public void WithdrawalAndReadmissionNeverReuseIdentityAndRequireExactSession()
    {
        var protection = new StoryProtection(); var session = Guid.NewGuid();
        var initial = protection.Epoch;
        protection.Admit(session, new[] { "owned" }, "ready");
        var admitted = protection.Epoch;
        Assert.NotSame(initial, admitted);
        Assert.True(protection.IsAdmitted(session, "owned"));
        Assert.False(protection.IsAdmitted(Guid.NewGuid(), "owned"));
        protection.WithdrawAll("unavailable");
        var withdrawn = protection.Epoch;
        Assert.NotSame(admitted, withdrawn);
        Assert.False(protection.IsAdmitted(session, "owned"));
        protection.Admit(session, new[] { "owned" }, "ready");
        Assert.NotSame(admitted, protection.Epoch);
        Assert.NotSame(withdrawn, protection.Epoch);
        Assert.True(protection.IsAdmitted(session, "owned"));
    }
}
