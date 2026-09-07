using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int CommittedDocuments = 22;
    private const int GeneratedDocuments = 2;
    /// <summary>The project directory every compiled document of this build must live under.</summary>
    private const string ProjectSegment = "/VGTravelJournal/";
    /// <summary>
    /// The ONLY documents that may have no committed source: the two files the SDK generates into
    /// the project's own obj/ tree. Anything else without a committed source is unaccounted for.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex GeneratedDocument = new(
        @"^VGTravelJournal/obj/[A-Za-z0-9._-]+/netstandard2\.1/(VGTravelJournal\.AssemblyInfo\.cs|\.NETStandard,Version=v2\.1\.AssemblyAttributes\.cs)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
    /// The sibling PDB really belongs to this DLL and carries a SHA-256 hash for every compiled
    /// document. This test alone proves only that those hashes EXIST; comparing them against the
    /// archived revision's committed sources is
    /// <see cref="TheCompiledDocumentHashesMatchTheCommittedSourcesOfThePinnedRevision"/>.
    /// Document PATHS are never emitted.
    /// </summary>
    [Fact]
    public void TheSiblingPdbBelongsToThisBinaryAndCarriesAHashForEveryCompiledDocument()
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
        Assert.Equal(CommittedDocuments, documents.Length - GeneratedDocuments);
        // The comparison phase pins the same revision the build embedded.
        Assert.Equal(PinnedRevision, PinnedInformationalVersion.Split('+')[1]);
    }

    /// <summary>
    /// The real source attestation: every committed document the PDB records is compared, by
    /// SHA-256, against the SAME file as it stands in the pinned archive revision. The comparison
    /// reads the revision through <c>git show</c>, never the working tree, so uncommitted local
    /// drift can neither satisfy nor break it. The archive is only READ; nothing is built.
    /// </summary>
    [Fact]
    public void TheCompiledDocumentHashesMatchTheCommittedSourcesOfThePinnedRevision()
    {
        var repository = ArchiveRepository();
        Assert.Equal(PinnedRevision, Git(repository, "rev-parse", PinnedRevision + "^{commit}").Trim());
        var pdbPath = Environment.GetEnvironmentVariable("VG_TRAVELJOURNAL_PDB");
        if (string.IsNullOrEmpty(pdbPath))
            throw new InvalidOperationException("Set VG_TRAVELJOURNAL_PDB to the sibling PDB of the pinned prebuilt (it is never deployed).");
        using var pdb = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdb);
        var reader = provider.GetMetadataReader();
        var compared = new List<string>();
        var generated = new List<string>();
        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            var relative = RepositoryRelativePath(reader.GetString(document.Name));
            if (GeneratedDocument.IsMatch(relative))
            {
                generated.Add(relative);
                continue;
            }
            var recorded = Convert.ToHexString(reader.GetBlobBytes(document.Hash));
            var committed = Convert.ToHexString(SHA256.HashData(GitShowBytes(repository, PinnedRevision + ":" + relative)));
            // Only the file NAME appears in a failure, never its path or its content.
            Assert.True(recorded == committed,
                "The compiled document '" + Path.GetFileName(relative) + "' does not match the pinned revision's committed source.");
            compared.Add(relative);
        }
        Assert.Equal(CommittedDocuments, compared.Count);
        Assert.Equal(compared.Count, compared.Distinct(StringComparer.Ordinal).Count());
        // Nothing is unaccounted for: exactly the two generated files may lack a committed source.
        Assert.Equal(GeneratedDocuments, generated.Count);
        Assert.Equal(CompiledDocuments, compared.Count + generated.Count);
        // Every committed .cs file of the project was compiled; none was dropped from the build.
        var tracked = Git(repository, "ls-tree", "-r", "--name-only", PinnedRevision, "--", "VGTravelJournal")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.EndsWith(".cs", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(tracked.OrderBy(path => path, StringComparer.Ordinal),
            compared.OrderBy(path => path, StringComparer.Ordinal));
    }

    /// <summary>
    /// The archive checkout to READ the pinned revision from: VG_TRAVELJOURNAL_REPO when supplied,
    /// otherwise the git repository the pinned binary itself lives in.
    /// </summary>
    private static string ArchiveRepository()
    {
        var configured = Environment.GetEnvironmentVariable("VG_TRAVELJOURNAL_REPO");
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Directory.Exists(Path.Combine(configured, ".git")))
                throw new InvalidOperationException("VG_TRAVELJOURNAL_REPO is not a git repository.");
            return Path.GetFullPath(configured);
        }
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(AssemblyPath))!);
            directory != null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
        }
        throw new InvalidOperationException("Set VG_TRAVELJOURNAL_REPO: the pinned binary is not inside an archive checkout.");
    }

    /// <summary>
    /// The path of a compiled document RELATIVE to the archive repository root. Build-machine
    /// absolute paths, separator style and any traversal are rejected rather than normalised away.
    /// </summary>
    private static string RepositoryRelativePath(string documentName)
    {
        var normalised = documentName.Replace('\\', '/');
        int project = normalised.IndexOf(ProjectSegment, StringComparison.Ordinal);
        Assert.True(project >= 0, "A compiled document lies outside the archived project directory.");
        var relative = normalised[(project + 1)..];
        Assert.DoesNotContain("..", relative, StringComparison.Ordinal);
        Assert.False(relative.StartsWith('/'), "A compiled document path did not resolve to a repository-relative path.");
        return relative;
    }

    /// <summary>Read-only git invocation. Arguments are passed as a list, never through a shell.</summary>
    private static Process StartGit(string repository, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--git-dir");
        start.ArgumentList.Add(Path.Combine(repository, ".git"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start git to read the archived revision.");
    }

    private static string Git(string repository, params string[] arguments)
    {
        using var process = StartGit(repository, arguments);
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "Reading the pinned archive revision failed: git " + arguments[0] + ".");
        return output;
    }

    private static byte[] GitShowBytes(string repository, string revisionPath)
    {
        using var process = StartGit(repository, "show", revisionPath);
        using var buffer = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(buffer);
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "The pinned revision has no committed source for a compiled document.");
        return buffer.ToArray();
    }
}
