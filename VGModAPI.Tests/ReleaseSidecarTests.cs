using System;
using System.IO;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ReleaseSidecarTests
{
    [Theory]
    [InlineData("{}", "stable")]
    [InlineData("not JSON", "stable")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"wrong\",\"channel\":\"stable\",\"updateUrl\":\"https://github.com/a/b/feed\"}", "stable")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"author.mod\",\"channel\":\"experimental\",\"updateUrl\":\"https://github.com/a/b/feed\"}", "stable")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"author.mod\",\"channel\":\"stable\",\"updateUrl\":\"https://github.com/a/b/feed\"}", "experimental")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"author.mod\",\"updateUrl\":\"https://evil.example/feed\"}", "stable")]
    public void RejectsMalformedWrongIdentityOrChannel(string text, string channel)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, text);
            Assert.Throws<FormatException>(() => ReleaseMetadata.Program.ValidateSidecar(path, "author.mod", channel, "https://github.com/a/b/releases/tag/v1.0"));
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("stable", "latest/download/update.json", true)]
    [InlineData("experimental", "download/updates-experimental/update.json", true)]
    [InlineData("stable", "download/updates-experimental/update.json", false)]
    [InlineData("experimental", "latest/download/update.json", false)]
    public void OfficialDiscoveryMustMatchPublicationChannel(string channel, string route, bool valid)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"schemaVersion\":1,\"pluginId\":\"vgmodapi\",\"channel\":\"" + channel + "\",\"updateUrl\":\"https://github.com/a/b/releases/" + route + "\"}");
            void Validate() => ReleaseMetadata.Program.ValidateSidecar(path, "vgmodapi", channel, "https://github.com/a/b/releases/tag/v1.0");
            if (valid) Validate(); else Assert.Throws<FormatException>(Validate);
        }
        finally { File.Delete(path); }
    }
}
