using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-consumer metadata evidence for the actual-consumer travel probe. The probe reads the
/// installed Anima build by reflection, so every member it touches is pinned here by its exact
/// declared shape rather than assumed from the assembly name, and the two structural facts the
/// probe's conclusions depend on are proven from the shipped IL:
///
/// 1. the consumer's visited-system map can only grow inside its public-event observer, so a
///    registry delta the probe measures really is a witnessed arrival and nothing else;
/// 2. the consumer owns no Harmony patch on the native travel/station surface, so there is no
///    direct hook that could record a visit behind the public API.
///
/// Run with VG_ANIMA_ASSEMBLY pointing at the owner-built VGAnima.dll (make check-consumer).
///
/// Reading is Cecil-only and read-only: the candidate is opened in memory, nothing is loaded into
/// this process and no consumer or dependency binary is ever copied into this repository. Cecil
/// still has to RESOLVE a few referenced assemblies to decode custom-attribute enum arguments
/// (<c>BepInEx.BepInDependency.DependencyFlags</c>, <c>Newtonsoft.Json.NullValueHandling</c>), so
/// the resolver gets an explicit, bounded search path instead of Cecil's implicit defaults; see
/// <see cref="SearchDirectories"/> and docs/checks.md.
/// </summary>
[Trait("Category", "InstalledConsumer")]
public sealed class InstalledAnimaTravelConsumerTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_ANIMA_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-consumer or set VG_ANIMA_ASSEMBLY to the built VGAnima.dll.");

    /// <summary>Extra dependency directories, supplied by make check-consumer (see docs/checks.md).</summary>
    private const string DependencyDirectoriesVariable = "VG_CONSUMER_DEPENDENCY_DIRS";

    /// <summary>
    /// The bounded, explicit directories the Cecil resolver may read, in order: the candidate's own
    /// output directory, the consumer project's own reference-link directory, this test assembly's
    /// output directory, and every existing directory named by <see cref="DependencyDirectoriesVariable"/>
    /// (the installed BepInEx core, the installed Managed references and any owner override). Cecil's
    /// implicit "."/"bin" probing is removed, so nothing outside this list is ever read.
    /// </summary>
    private static IReadOnlyList<string> SearchDirectories(string consumerPath)
    {
        var directories = new List<string>();
        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            var full = Path.GetFullPath(candidate!);
            if (Directory.Exists(full) && !directories.Contains(full, StringComparer.Ordinal)) directories.Add(full);
        }
        var output = Path.GetDirectoryName(Path.GetFullPath(consumerPath));
        Add(output);
        // bin/<configuration>/<targetFramework> -> the project's own lib directory, which is where
        // the consumer's local reference links (game assembly, API abstractions, journal) live.
        if (output != null) Add(Path.Combine(output, "..", "..", "..", "lib"));
        Add(AppContext.BaseDirectory);
        foreach (var directory in (Environment.GetEnvironmentVariable(DependencyDirectoriesVariable) ?? string.Empty)
            .Split(Path.PathSeparator))
            Add(directory);
        return directories;
    }

    /// <summary>
    /// Resolves ONLY from <see cref="SearchDirectories"/> and reports a miss as an actionable
    /// harness failure (what was needed, what was searched, how to extend it) instead of a bare
    /// Cecil resolution stack trace.
    /// </summary>
    private sealed class BoundedConsumerResolver : DefaultAssemblyResolver
    {
        private readonly IReadOnlyList<string> _directories;
        internal BoundedConsumerResolver(IReadOnlyList<string> directories)
        {
            _directories = directories;
            // Drop Cecil's implicit "."/"bin" probing so nothing outside the explicit list is read.
            foreach (var directory in GetSearchDirectories()) RemoveSearchDirectory(directory);
            foreach (var directory in directories) AddSearchDirectory(directory);
        }
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            try { return base.Resolve(name); }
            catch (AssemblyResolutionException error)
            {
                throw new InvalidOperationException("Cecil could not resolve the consumer dependency '"
                    + name.FullName + "' that decoding this metadata requires. Searched: "
                    + string.Join(", ", _directories)
                    + ". Point ANIMA_DEPENDENCY_DIRS at the directory holding it (see docs/checks.md).", error);
            }
        }
    }

    /// <summary>The candidate opened read-only with the bounded resolver; disposes both.</summary>
    private sealed class ConsumerRead : IDisposable
    {
        private readonly BoundedConsumerResolver _resolver;
        internal AssemblyDefinition Assembly { get; }
        internal ConsumerRead(string path)
        {
            _resolver = new BoundedConsumerResolver(SearchDirectories(path));
            // InMemory keeps the candidate file unlocked and guarantees it is never written.
            Assembly = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = _resolver, InMemory = true, ReadSymbols = false });
        }
        public void Dispose() { Assembly.Dispose(); _resolver.Dispose(); }
    }

    private static ConsumerRead ReadConsumer() => new(AssemblyPath);

    private const string Plugin = "VGAnima.Plugin";
    private const string Observer = "VGAnima.Persistence.SystemVisitObserver";
    private const string Registry = "VGAnima.Persistence.PersistedBrokerRegistry";
    private const string VisitedSystem = "VGAnima.Persistence.VisitedSystem";
    private const string Schema = "VGAnima.Persistence.SidecarSchema";
    private const string SaveLoadPatch = "VGAnima.Patches.SaveLoadPatch";
    private const string SaveWritePatch = "VGAnima.Patches.SaveWritePatch";

    [Fact]
    public void ConsumerIdentityAndApiDependencyAreThePinnedTravelObservingShape()
    {
        using var consumer = ReadConsumer();
        var assembly = consumer.Assembly;
        Assert.Equal("VGAnima", assembly.Name.Name);
        Assert.Equal("0.4.0.0", assembly.Name.Version.ToString());
        var plugin = assembly.MainModule.GetType(Plugin) ?? throw new InvalidOperationException("Missing VGAnima.Plugin.");
        var dependency = Assert.Single(plugin.CustomAttributes, attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInDependency"
            && attribute.ConstructorArguments.Count == 2
            && (string?)attribute.ConstructorArguments[0].Value == "vgmodapi");
        Assert.Equal("0.1.9", (string?)dependency.ConstructorArguments[1].Value);
    }

    [Fact]
    public void EveryConsumerMemberTheProbeReflectsHasItsDeclaredShape()
    {
        using var consumer = ReadConsumer();
        var assembly = consumer.Assembly;
        var module = assembly.MainModule;
        TypeDefinition Type(string name) => module.GetType(name) ?? throw new InvalidOperationException("Missing type: " + name);
        void Field(string owner, string name, string type, bool isStatic = false)
        {
            var field = Assert.Single(Type(owner).Fields, candidate => candidate.Name == name);
            Assert.Equal(type, field.FieldType.FullName);
            Assert.Equal(isStatic, field.IsStatic);
        }
        void Property(string owner, string name, string type)
        {
            var property = Assert.Single(Type(owner).Properties, candidate => candidate.Name == name);
            Assert.Equal(type, property.PropertyType.FullName);
            Assert.NotNull(property.GetMethod);
            Assert.Empty(property.Parameters);
        }
        MethodDefinition Method(string owner, string name, string returnType, params string[] parameters)
        {
            var method = Assert.Single(Type(owner).Methods, candidate => candidate.Name == name
                && candidate.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameters));
            Assert.Equal(returnType, method.ReturnType.FullName);
            return method;
        }

        // The live state the probe samples: the recording gate the prompt builder itself reads, the
        // observer instance, and the provider/authoring state a visit-only fault must not change.
        Property(Plugin, "VisitHistoryRecording", "System.Boolean");
        Property(Plugin, "PersistedRegistry", Registry);
        Property(Plugin, "SidecarIO", "VGAnima.Persistence.SidecarIO");
        Property(Plugin, "Clock", "VGAnima.Persistence.IClock");
        Property(Plugin, "Gatherer", "VGAnima.Llm.ContextGatherer");
        Property(Plugin, "GameStateView", "VGAnima.Llm.IGameStateView");
        Property(Plugin, "MissionJournalBridge", "VGAnima.MissionJournal.VgMissionJournalBridge");
        Field(Plugin, "_visitObserver", Observer);
        Field(Plugin, "_active", "System.Boolean");
        Field(Plugin, "_harmony", "HarmonyLib.Harmony");
        Field(Plugin, "_loadSafetyHarmony", "HarmonyLib.Harmony");

        // The consumer's own documented visit-only fault path and its per-session leg latch.
        Method(Observer, "Receive", "System.Void", "VGModAPI.TravelTransition");
        Property(Observer, "Faulted", "System.Boolean");
        Property(Observer, "IsRecording", "System.Boolean");
        Field(Observer, "_session", "System.Nullable`1<System.Guid>");
        Field(Observer, "_countedLegs", "System.Collections.Generic.HashSet`1<System.Guid>");

        // The visited-system history and its persisted v4 shape.
        Property(Registry, "VisitedSystems", "System.Collections.Generic.IReadOnlyDictionary`2<System.String," + VisitedSystem + ">");
        Method(Registry, "NoteSystemVisit", "System.Void", "System.String", "System.String", "System.Double");
        foreach (var (name, type) in new[]
        {
            ("Guid", "System.String"), ("Name", "System.String"), ("VisitCount", "System.Int32"),
            ("FirstVisitGameSeconds", "System.Double"), ("LastVisitGameSeconds", "System.Double")
        }) Property(VisitedSystem, name, type);
        var version = Assert.Single(Type(Schema).Fields, field => field.Name == "CurrentVersion");
        Assert.True(version.IsStatic && version.HasConstant);
        Assert.Equal(4, Convert.ToInt32(version.Constant));
        Property(Schema, "Version", "System.Int32");
        Property(Schema, "VisitedSystems", VisitedSystem + "[]");
        Method("VGAnima.Persistence.SidecarIO", "Read", "VGAnima.Persistence.SidecarReadResult", "System.String");
        Property("VGAnima.Persistence.SidecarReadResult", "Status", "VGAnima.Persistence.SidecarReadStatus");
        Property("VGAnima.Persistence.SidecarReadResult", "Schema", Schema);
        Assert.True(Method("VGAnima.Persistence.SidecarPathResolver", "From", "System.String", "System.String").IsStatic);

        // The load/save hooks that must survive a visit-only fault, and the observer handle the
        // load hook owns while recording is live.
        Field(SaveLoadPatch, "VisitObserver", Observer, isStatic: true);
        Field(SaveWritePatch, "CanWrite", "System.Func`1<System.Boolean>", isStatic: true);

        // The SHARED production decision the probe invokes: parameterless, so the probe cannot
        // supply a substitute recording flag or visit history, and it reads the live gate itself.
        var decision = Method("VGAnima.Llm.RegionalRecognition", "ForCurrentContext",
            "System.Collections.Generic.IReadOnlyList`1<VGAnima.Llm.LlmRegionallyKnownEntry>");
        Assert.True(decision.IsStatic);

        // The gather-time decision the probe evaluates through the consumer's own members.
        var build = Method("VGAnima.Llm.RegionallyKnownBuilder", "Build",
            "System.Collections.Generic.IReadOnlyList`1<VGAnima.Llm.LlmRegionallyKnownEntry>",
            "System.Collections.Generic.IReadOnlyDictionary`2<System.String," + VisitedSystem + ">",
            "VGAnima.MissionJournal.VgMissionJournalBridge", "System.Double");
        Assert.True(build.IsStatic);
        var gather = Assert.Single(Type("VGAnima.Llm.ContextGatherer").Methods, method => method.Name == "Gather");
        Assert.Equal(7, gather.Parameters.Count);
        Assert.Equal("VGAnima.Llm.IGameStateView", gather.Parameters[0].ParameterType.FullName);
        Assert.Equal("VGAnima.Llm.BrokerInfo", gather.Parameters[1].ParameterType.FullName);
        Assert.Equal("System.Collections.Generic.IReadOnlyList`1<VGAnima.Llm.LlmRegionallyKnownEntry>",
            gather.Parameters[6].ParameterType.FullName);
        var broker = Assert.Single(Type("VGAnima.Llm.BrokerInfo").Methods, method => method.IsConstructor && method.Parameters.Count == 4);
        Assert.Equal(new[] { "System.String", "System.Boolean", "System.String", "System.String" },
            broker.Parameters.Select(parameter => parameter.ParameterType.FullName).ToArray());
        var contextProperty = Assert.Single(Type("VGAnima.Llm.LlmContext").Properties, property => property.Name == "RegionallyKnown");
        var jsonProperty = Assert.Single(contextProperty.CustomAttributes, attribute => attribute.AttributeType.Name == "JsonPropertyAttribute");
        Assert.Equal("regionally_known", (string?)jsonProperty.ConstructorArguments[0].Value);
    }

    [Fact]
    public void TheProbeAndTheProductionPromptPathShareOneRegionalRecognitionDecision()
    {
        using var consumer = ReadConsumer();
        var module = consumer.Assembly.MainModule;
        // Nothing may re-derive the gate: only the shared decision builds the window, and the
        // consumer's own bar context path composes through that same method the probe invokes.
        Assert.Equal(new[] { "VGAnima.Llm.RegionalRecognition.ForCurrentContext" },
            Callers(module, "VGAnima.Llm.RegionallyKnownBuilder", "Build").ToArray());
        Assert.Contains("VGAnima.Patches.BarRefreshPatches.StartInjectMissionBroker",
            Callers(module, "VGAnima.Llm.RegionalRecognition", "ForCurrentContext"));
        // The decision reads the live recording gate and registry itself.
        var body = AllTypes(module).Single(type => type.FullName == "VGAnima.Llm.RegionalRecognition")
            .Methods.Single(method => method.Name == "ForCurrentContext").Body.Instructions
            .Select(instruction => (instruction.Operand as MethodReference)?.Name)
            .Where(name => name != null)
            .ToArray();
        foreach (var read in new[] { "get_Instance", "get_VisitHistoryRecording", "get_PersistedRegistry", "get_VisitedSystems", "get_GameSeconds" })
            Assert.Contains(read, body);
    }

    [Fact]
    public void TheVisitedSystemMapCanOnlyGrowInsideThePublicEventObserver()
    {
        using var consumer = ReadConsumer();
        var assembly = consumer.Assembly;
        var module = assembly.MainModule;
        var callers = Callers(module, "NoteSystemVisit").ToArray();
        Assert.Equal(new[] { Observer + ".Observe" }, callers);
        // The whole-map replacement is the load path only, so a rollback really is the loaded slot's
        // own history rather than an in-place edit somewhere else in the consumer.
        Assert.Equal(new[] { SaveLoadPatch + ".Prefix" }, Callers(module, "LoadVisitedSystems").ToArray());
    }

    [Fact]
    public void TheConsumerOwnsNoDirectNativeTravelOrStationHarmonyPatch()
    {
        using var consumer = ReadConsumer();
        var assembly = consumer.Assembly;
        var refusals = new List<string>();
        foreach (var type in AllTypes(assembly.MainModule))
        {
            foreach (var target in PatchTargets(type.CustomAttributes, type.Methods.SelectMany(method => method.CustomAttributes)))
            {
                var refusal = AnimaTravelReceipt.RefuseConsumerTravelPatch(target.Type, target.Method);
                if (refusal != null) refusals.Add(type.FullName + " -> " + refusal);
            }
        }
        Assert.Empty(refusals);
        // Independently of the attributes: the shipped IL must not reference the native travel
        // manager at all, so no runtime-built patch or direct call can record a visit behind the API.
        var referenced = assembly.MainModule.GetTypeReferences().Select(reference => reference.FullName).ToArray();
        Assert.DoesNotContain("Behaviour.Managers.TravelManager", referenced);
        Assert.Contains("VGModAPI.ITravelEvents", referenced);
    }

    /// <summary>
    /// Every type in the module at ANY nesting depth. A single level would miss a closure or state
    /// machine the compiler generates inside an already nested type, which is exactly where a call
    /// the scan is meant to find could hide.
    /// </summary>
    internal static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        IEnumerable<TypeDefinition> Walk(TypeDefinition type)
        {
            yield return type;
            foreach (var nested in type.NestedTypes)
                foreach (var deeper in Walk(nested)) yield return deeper;
        }
        return module.Types.SelectMany(Walk);
    }

    // Declared Harmony patch targets: the class-level typeof(...) plus any method name argument.
    private static IEnumerable<(string Type, string Method)> PatchTargets(params IEnumerable<CustomAttribute>[] attributeSets)
    {
        foreach (var attributes in attributeSets)
            foreach (var attribute in attributes.Where(candidate => candidate.AttributeType.Name == "HarmonyPatch"))
            {
                var type = attribute.ConstructorArguments.FirstOrDefault(argument => argument.Value is TypeReference).Value as TypeReference;
                var method = attribute.ConstructorArguments.FirstOrDefault(argument => argument.Value is string).Value as string;
                if (type != null || method != null) yield return (type?.FullName ?? "", method ?? "");
            }
    }

    // Fully qualified "Type.Method" of every consumer method (including compiler-generated
    // iterators and lambdas) that calls the named member.
    internal static IEnumerable<string> Callers(ModuleDefinition module, string member) => Callers(module, null, member);

    /// <summary>Callers of a member, optionally restricted to one declaring type.</summary>
    internal static IEnumerable<string> Callers(ModuleDefinition module, string? declaringType, string member)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
                if (method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call && call.Name == member
                    && (declaringType == null || call.DeclaringType.FullName == declaringType)))
                {
                    var owner = type;
                    var name = method.Name;
                    // Attribute a state machine or closure back to the method that declares it.
                    while (owner.DeclaringType != null && owner.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        name = owner.Name.Substring(1, owner.Name.IndexOf('>') - 1);
                        owner = owner.DeclaringType;
                    }
                    result.Add(owner.FullName + "." + name);
                }
        return result;
    }
}
