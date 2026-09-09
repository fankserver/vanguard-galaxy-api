using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ModInformationTests
{
    private static LoadedPluginInformation Plugin(string id, bool? hard = null) => new(id, "Loader name " + id,
        new Version(1, 2), Path.Combine(Path.GetTempPath(), "mods", "package.dll"), hard == null
            ? Array.Empty<ModDependencyInformation>() : new[] { new ModDependencyInformation(ModApi.PluginId, new Version(0, 1, 9), hard.Value) });
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    private static ModAuthorMetadata Parse(string value) => ModMetadataCodec.Parse(Bytes(value), "a");

    [Fact]
    public void InventoryWaitsForRefreshIncludesSelfAndOnlyDirectDeclaredConsumers()
    {
        var plugins = new List<LoadedPluginInformation> { Plugin(ModApi.PluginId) }; var calls = 0;
        using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => { ++calls; return plugins; }, _ => null);
        Assert.Empty(catalog.Inventory.Entries); Assert.Equal(0, calls);
        plugins.AddRange(new[] { Plugin("a", true), Plugin("b", false), Plugin("not-a-consumer") });
        catalog.Refresh();
        Assert.Equal(new[] { "a", "b", ModApi.PluginId }, catalog.Inventory.Entries.Select(x => x.PluginId));
        Assert.All(catalog.Inventory.Entries, row => Assert.Equal(ModMetadataStatus.Missing, row.MetadataStatus));
        Assert.Equal("Loader name a", catalog.Inventory.Entries[0].Name);
        Assert.Equal(new Version(1, 2), catalog.Inventory.Entries[0].InstalledVersion);
        Assert.True(catalog.Inventory.Entries[0].Dependencies[0].HardDependency);
        Assert.False(catalog.Inventory.Entries[1].Dependencies[0].HardDependency);
        var old = catalog.Inventory.Entries; plugins.Clear(); catalog.Refresh();
        Assert.Empty(catalog.Inventory.Entries); Assert.Equal(3, old.Count);
    }

    [Fact]
    public void MetadataFailureIsPerRowAndInstalledIdentityCannotBeOverridden()
    {
        using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { Plugin("a", true), Plugin("b", true), Plugin("c", false) },
            path => path.EndsWith("a.vgmod.json", StringComparison.Ordinal) ? Bytes("{\"schemaVersion\":1,\"pluginId\":\"a\",\"installedVersion\":\"99.0\"}") :
                path.EndsWith("b.vgmod.json", StringComparison.Ordinal) ? throw new IOException("private path or secret must not enter the snapshot") :
                Bytes("{\"schemaVersion\":1,\"pluginId\":\"c\",\"author\":\"Someone\"}"));
        catalog.Refresh();
        Assert.Equal(new[] { ModMetadataStatus.Invalid, ModMetadataStatus.Unreadable, ModMetadataStatus.Available }, catalog.Inventory.Entries.Select(r => r.MetadataStatus));
        Assert.All(catalog.Inventory.Entries, row => Assert.Equal(new Version(1, 2), row.InstalledVersion));
        Assert.Equal("Someone", catalog.Inventory.Entries[2].Metadata!.Author);
    }

    [Fact]
    public void UnsupportedPathFailureIsIsolatedAndPublicNullArgumentsAreDiagnosed()
    {
        using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { Plugin("a", true), Plugin("b", true) },
            path => path.EndsWith("a.vgmod.json", StringComparison.Ordinal) ? throw new NotSupportedException("Private path") : null);
        catalog.Refresh();
        Assert.Equal(new[] { ModMetadataStatus.Invalid, ModMetadataStatus.Missing }, catalog.Inventory.Entries.Select(r => r.MetadataStatus));
        Assert.Throws<ArgumentNullException>(() => new ModInformation("a", "a", new Version(1, 0), null!, null, ModMetadataStatus.Missing));
        Assert.Throws<ArgumentNullException>(() => new ModDependencyInformation(null!, null, false));
    }

    [Fact]
    public void SharedDllUsesOneDeterministicSidecarPerGuid()
    {
        var paths = new List<string>();
        using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { Plugin("a", true), Plugin("b", true) }, path => { paths.Add(path); return null; });
        catalog.Refresh();
        Assert.Equal(new[] { "a.vgmod.json", "b.vgmod.json" }, paths.Select(Path.GetFileName));
        Assert.Single(paths.Select(Path.GetDirectoryName).Distinct());
        Assert.Throws<FormatException>(() => ModInformationCatalog.MetadataPath("relative.dll", "a"));
        Assert.Throws<FormatException>(() => ModInformationCatalog.MetadataPath(Path.GetFullPath("package.dll"), "../escape"));
    }

    [Fact]
    public void DuplicateLoaderIdsRefuseRatherThanPickingMetadataFromArbitraryDll()
    {
        using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { Plugin("a", true), Plugin("a", true) }, _ => null);
        Assert.Equal(ModInventoryStatus.RefreshFailed, catalog.Refresh().Status); Assert.Empty(catalog.Inventory.Entries);
    }

    [Fact]
    public void DisposalAndForeignThreadCannotRefresh()
    {
        var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { Plugin("a", true) }, _ => null);
        catalog.Refresh(); catalog.Dispose(); catalog.Dispose(); Assert.Empty(catalog.Inventory.Entries);
        Assert.Equal(ModInventoryStatus.Stopped, catalog.Refresh().Status);
        using var live = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => Array.Empty<LoadedPluginInformation>(), _ => null);
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(() => live.Refresh()));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(live.Dispose));
    }

    [Fact]
    public void FileReaderBoundsBytesAndPreservesMissingRows()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var plugin = new LoadedPluginInformation("a", "a", new Version(1, 0), Path.Combine(directory, "package.dll"), new[] { new ModDependencyInformation(ModApi.PluginId, null, false) });
            using var catalog = new ModInformationCatalog(new LifecycleHub((_, _) => { }), () => new[] { plugin });
            catalog.Refresh(); Assert.Equal(ModMetadataStatus.Missing, catalog.Inventory.Entries[0].MetadataStatus);
            File.WriteAllBytes(Path.Combine(directory, "a.vgmod.json"), new byte[ModMetadataCodec.MaxBytes + 1]);
            catalog.Refresh(); Assert.Equal(ModMetadataStatus.Invalid, catalog.Inventory.Entries[0].MetadataStatus);
            File.WriteAllText(Path.Combine(directory, "a.vgmod.json"), "{\"schemaVersion\":1,\"pluginId\":\"a\"}", new UTF8Encoding(false));
            catalog.Refresh(); Assert.Equal(ModMetadataStatus.Available, catalog.Inventory.Entries[0].MetadataStatus);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MetadataSupportsUnicodeAndExplicitExperimentalChannel()
    {
        var metadata = Parse("{\"schemaVersion\":1,\"pluginId\":\"a\",\"description\":\"Hi \\ud83d\\ude80\\nNext\",\"projectUrl\":\"https://github.com/owner/repo\",\"updateUrl\":\"https://example.org/feed.json\",\"channel\":\"experimental\"}");
        Assert.Equal("Hi 🚀\nNext", metadata.Description); Assert.Equal("experimental", metadata.Channel);
        Assert.Equal("stable", Parse("{\"schemaVersion\":1,\"pluginId\":\"a\"}").Channel);
    }

    [Theory]
    [InlineData("{}")] [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"pluginId\":\"a\"}")]
    [InlineData("{\"schemaVersion\":\"1\",\"pluginId\":\"a\"}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"wrong\"}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\",\"pluginId\":\"a\"}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\",\"author\":null}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\",}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\"} trailing")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\",\"description\":\"\\ud800\"}")]
    [InlineData("{\"schemaVersion\":1,\"pluginId\":\"a\",\"channel\":\"arbitrary\"}")]
    public void InvalidMetadataIsRefused(string text)
    {
        var error = Record.Exception(() => Parse(text));
        Assert.True(error is FormatException or EncoderFallbackException);
    }

    [Fact]
    public void InvalidEncodingAndFieldBoundsAreRefused()
    {
        Assert.Throws<DecoderFallbackException>(() => ModMetadataCodec.Parse(new byte[] { 0xff }, "a"));
        Assert.Throws<FormatException>(() => ModMetadataCodec.Parse(new byte[ModMetadataCodec.MaxBytes + 1], "a"));
        Assert.Throws<FormatException>(() => Parse("{\"schemaVersion\":1,\"pluginId\":\"a\",\"author\":\"" + new string('a', 257) + "\"}"));
        Assert.Throws<FormatException>(() => Parse("{\"schemaVersion\":1,\"pluginId\":\"a\",\"description\":\"" + new string('a', 4097) + "\"}"));
        Assert.Throws<FormatException>(() => Parse("{\"schemaVersion\":1,\"pluginId\":\"a\",\"description\":\"\\u0000\"}"));
    }

    [Theory]
    [InlineData("http://example.org")][InlineData("https://user:password@example.org")]
    [InlineData("https://localhost/feed")][InlineData("https://127.0.0.1/feed")][InlineData("https://[::1]/feed")]
    [InlineData("file:///tmp/feed")][InlineData("https://example.org:444/feed")]
    [InlineData("https://host.local/feed")][InlineData("https://example.org/#fragment")]
    [InlineData("https://example.org/\\escape")]
    public void UnsafeUrlShapesAreRejected(string url) => Assert.False(ModMetadataCodec.IsPublicHttpsUrl(url));

    [Theory]
    [InlineData("1.2", "1.2.0.0")][InlineData("1.2.3", "1.2.3.0")][InlineData("1.2.3.4", "1.2.3.4")]
    public void NumericVersionsPadUnspecifiedComponentsWithZero(string text, string normalized)
    { Assert.True(ModMetadataCodec.TryVersion(text, out var version)); Assert.Equal(normalized, version!.ToString()); }

    [Theory]
    [InlineData("1")][InlineData("v1.2")][InlineData("1.2-beta")][InlineData("1.2.3.4.5")][InlineData("1.-2")][InlineData(" 1.2")]
    public void NonLoaderVersionsAreNotSilentlyConverted(string text) => Assert.False(ModMetadataCodec.TryVersion(text, out _));
}
