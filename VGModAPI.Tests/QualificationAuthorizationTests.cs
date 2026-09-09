using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace VGModAPI.Tests;
public sealed class QualificationAuthorizationTests
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(60, true)]
    [InlineData(14400, true)]
    [InlineData(14401, false)]
    public void AuthorizationBindsRunAndBoundedExpiry(int seconds, bool accepted)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1800000000); string run = Guid.NewGuid().ToString("D");
        var bytes = Encoding.UTF8.GetBytes("vgmodapi-world-empty-v1\n" + run + "\n" + now.AddSeconds(seconds).ToUnixTimeSeconds() + "\n" + string.Join("\n", Enumerable.Repeat(new string('a', 64), 8)));
        if (accepted) Assert.Equal(8, QualificationAuthorization.Parse(bytes, run, now).Hashes.Length);
        else Assert.Throws<InvalidDataException>(() => QualificationAuthorization.Parse(bytes, run, now));
        Assert.Throws<InvalidDataException>(() => QualificationAuthorization.Parse(bytes, Guid.NewGuid().ToString("D"), now));
        Assert.Throws<InvalidDataException>(() => QualificationAuthorization.Parse(new byte[4097], run, now));
        var invalidHash = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(new string('a', 64), "bad-hash"));
        Assert.Throws<InvalidDataException>(() => QualificationAuthorization.Parse(invalidHash, run, now));
    }
}
