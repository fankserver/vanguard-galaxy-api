using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Mono.Cecil;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Opt-in, READ-ONLY attestation of the archived TravelJournal prebuilt the comparison phase pins.
/// The archive is never built, edited, reactivated, migrated or bridged here: this only reads the
/// bytes of the single accepted binary (and, when supplied, its sibling PDB) and confirms that the
/// launcher's pins describe it.
///
/// Run with VG_TRAVELJOURNAL_ASSEMBLY (optionally VG_TRAVELJOURNAL_PDB and VG_GAME_ASSEMBLY):
/// <c>make check-archive</c>. Excluded from the default test run because it needs a local binary.
/// No path, private game code or binary content is ever emitted into an assertion message.
/// </summary>
[Trait("Category", "InstalledArchive")]
public sealed class InstalledTravelJournalArchiveTests
{
    /// <summary>The one accepted archived build; a rebuild would change this and must be refused.</summary>
    private const string PinnedSha256 = "f253c3eefb967af7a1472dfb48bd926bff14b84b7219208facdc594387b1fdad";
    private const string PinnedRevision = "818d8b7e13a7841703bdd99e173e7dd993f6895c";
    private const string PinnedInformationalVersion = "0.1.0+" + PinnedRevision;
    private const string PinnedAssemblyVersion = "0.1.0.0";
    private const string PinnedPluginVersion = "0.2.0";
    /// <summary>Compiled documents the build attested: 22 committed sources plus 2 SDK-generated files.</summary>
    private const int CompiledDocuments = 24;
    private const int GeneratedDocuments = 2;

    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_TRAVELJOURNAL_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-archive or set VG_TRAVELJOURNAL_ASSEMBLY to the pinned prebuilt VGTravelJournal.dll.");

    [Fact]
    public void ThePinnedArchivedBinaryIsExactlyTheAcceptedBuild()
    {
        using var stream = File.OpenRead(AssemblyPath);
        using var sha = SHA256.Create();
        Assert.Equal(PinnedSha256, Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant());
    }

    [Fact]
    public void ItsIdentityRevisionAndPluginVersionMatchTheLauncherPins()
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var directory in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(directory);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(AssemblyPath))!);
        foreach (var directory in (Environment.GetEnvironmentVariable("VG_CONSUMER_DEPENDENCY_DIRS") ?? string.Empty)
            .Split(Path.PathSeparator).Where(Directory.Exists))
            resolver.AddSearchDirectory(directory);
        using (resolver)
        using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(AssemblyPath,
            new ReaderParameters { AssemblyResolver = resolver, InMemory = true, ReadSymbols = false }))
        {
            Assert.Equal("VGTravelJournal", assembly.Name.Name);
            Assert.Equal(PinnedAssemblyVersion, assembly.Name.Version.ToString());
            var informational = Assert.Single(assembly.CustomAttributes,
                attribute => attribute.AttributeType.FullName == "System.Reflection.AssemblyInformationalVersionAttribute");
            Assert.Equal(PinnedInformationalVersion, (string?)informational.ConstructorArguments[0].Value);
            var plugin = assembly.MainModule.GetType("VGTravelJournal.Plugin")
                ?? throw new InvalidOperationException("Missing VGTravelJournal.Plugin.");
            var bepInPlugin = Assert.Single(plugin.CustomAttributes, attribute => attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
            Assert.Equal("vgtraveljournal", (string?)bepInPlugin.ConstructorArguments[0].Value);
            // The plugin version deliberately differs from the assembly version; both are pinned.
            Assert.Equal(PinnedPluginVersion, (string?)bepInPlugin.ConstructorArguments[2].Value);
        }
    }

    [Fact]
    public void ItsDeclaredPatchTargetsStillResolveAgainstTheInstalledGame()
    {
        var game = Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY");
        if (string.IsNullOrEmpty(game))
            throw new InvalidOperationException("Set VG_GAME_ASSEMBLY so the archived plugin's startup bindings can be checked read-only.");
        using var module = Mono.Cecil.ModuleDefinition.ReadModule(game, new ReaderParameters { InMemory = true, ReadSymbols = false });
        void Target(string type, string method)
        {
            var declaring = module.GetType(type) ?? throw new InvalidOperationException("Missing type: " + type);
            Assert.Contains(declaring.Methods, candidate => candidate.Name == method);
        }
        // The six methods the archived plugin patches. A missing one is a startup binding failure to
        // DIAGNOSE, never a reason to repair the archive.
        Target("Behaviour.Managers.TravelManager", "JumpToSystem");
        Target("Source.Util.SaveGameFile", "LoadSaveGame");
        Target("Source.Util.SaveGame", "Store");
        Target("Behaviour.Bootstrap.SceneLoader", "ToggleSpaceStationInterior");
        Target("SpacestationExteriorManager", "StartUndocking");
        Target("Behaviour.Managers.BasePoiManager", "SpaceshipHasArrived");
        // Its own sidecar path derivation follows this static, which the sandbox guard redirects.
        Assert.Contains((module.GetType("Source.Util.SaveGame")
            ?? throw new InvalidOperationException("Missing SaveGame.")).Fields, field => field.Name == "SavesPath" && field.IsStatic);
    }

    /// <summary>
    /// Optional source attestation: the sibling PDB really belongs to this DLL and carries SHA-256
    /// document hashes for every compiled file. Document PATHS are never emitted.
    /// </summary>
    [Fact]
    public void TheSiblingPdbBelongsToThisBinaryAndAttestsItsCompiledSources()
    {
        var pdbPath = Environment.GetEnvironmentVariable("VG_TRAVELJOURNAL_PDB");
        if (string.IsNullOrEmpty(pdbPath))
            throw new InvalidOperationException("Set VG_TRAVELJOURNAL_PDB to the sibling PDB of the pinned prebuilt (it is never deployed).");
        Guid codeView = Guid.Empty;
        uint stamp = 0;
        using (var dll = File.OpenRead(AssemblyPath))
        using (var pe = new PEReader(dll))
        {
            var entry = pe.ReadDebugDirectory().Single(candidate => candidate.Type == DebugDirectoryEntryType.CodeView);
            var data = pe.ReadCodeViewDebugDirectoryData(entry);
            codeView = data.Guid;
            stamp = entry.Stamp;
            Assert.Contains(pe.ReadDebugDirectory(), candidate => candidate.Type == DebugDirectoryEntryType.Reproducible);
        }
        using var pdb = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        var reader = provider.GetMetadataReader();
        var id = reader.DebugMetadataHeader!.Id;
        var expected = codeView.ToByteArray().Concat(BitConverter.GetBytes(stamp)).ToArray();
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(id.ToArray()));
        var documents = reader.Documents.Select(handle => reader.GetDocument(handle)).ToArray();
        Assert.Equal(CompiledDocuments, documents.Length);
        // SHA-256 for every document, and the generated-file count is exactly the two the SDK emits.
        var sha256 = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");
        Assert.All(documents, document => Assert.Equal(sha256, reader.GetGuid(document.HashAlgorithm)));
        Assert.All(documents, document => Assert.NotEmpty(reader.GetBlobBytes(document.Hash)));
        Assert.Equal(GeneratedDocuments, documents.Count(document =>
            reader.GetString(document.Name).Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)));
        // The comparison phase pins the same revision the build embedded.
        Assert.Equal(PinnedRevision, PinnedInformationalVersion.Split('+')[1]);
    }
}
