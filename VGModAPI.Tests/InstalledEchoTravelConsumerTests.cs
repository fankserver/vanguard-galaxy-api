using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using VGModAPI.Qualification;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-consumer metadata evidence for the actual-consumer Echo arrival-snap probe. The probe
/// reads the installed Echo build by reflection, so every member it touches is pinned here by its
/// exact declared shape, and the structural facts its conclusions depend on are proven from the
/// shipped IL:
///
/// 1. the retired timing hook is really gone — the timing patch class declares no Harmony patch on
///    the native route boundary — while Echo's unrelated automation keeps its own patches, which
///    are recorded rather than refused;
/// 2. the timer write is reachable only from the API observer path, never from a native hook;
/// 3. no always-JIT plugin member names a VGModAPI type, which is what lets the same build load
///    with the API absent (the separate MissingApi control).
///
/// Reading is Cecil-only and read-only, with the same bounded, explicit dependency search path the
/// Anima consumer tests use (see docs/checks.md). Run with VG_ECHO_ASSEMBLY (make check-consumer).
/// </summary>
[Trait("Category", "InstalledConsumer")]
public sealed class InstalledEchoTravelConsumerTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_ECHO_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-consumer or set VG_ECHO_ASSEMBLY to the built VGEcho.dll.");

    private const string Plugin = "VGEcho.Plugin";
    private const string TimingPatches = EchoTravelReceipt.TimingPatchClass;
    private const string Observer = "VGEcho.Travel.TravelArrivalObserver";
    private const string RouteBoundary = "TravelToNextWaypoint";

    private static IEnumerable<string> DependencyDirectories(string consumerPath)
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
        if (output != null) Add(Path.Combine(output, "..", "..", "..", "lib"));
        Add(AppContext.BaseDirectory);
        foreach (var directory in (Environment.GetEnvironmentVariable("VG_CONSUMER_DEPENDENCY_DIRS") ?? string.Empty)
            .Split(Path.PathSeparator))
            Add(directory);
        return directories;
    }

    private sealed class BoundedResolver : DefaultAssemblyResolver
    {
        private readonly IReadOnlyList<string> _directories;
        internal BoundedResolver(IReadOnlyList<string> directories)
        {
            _directories = directories;
            foreach (var directory in GetSearchDirectories()) RemoveSearchDirectory(directory);
            foreach (var directory in directories) AddSearchDirectory(directory);
        }
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            try { return base.Resolve(name); }
            catch (AssemblyResolutionException error)
            {
                throw new InvalidOperationException("Cecil could not resolve the consumer dependency '" + name.FullName
                    + "' that decoding this metadata requires. Searched: " + string.Join(", ", _directories)
                    + ". Point ECHO_DEPENDENCY_DIRS at the directory holding it (see docs/checks.md).", error);
            }
        }
    }

    private sealed class ConsumerRead : IDisposable
    {
        private readonly BoundedResolver _resolver;
        internal AssemblyDefinition Assembly { get; }
        internal ConsumerRead(string path)
        {
            _resolver = new BoundedResolver(DependencyDirectories(path).ToList());
            Assembly = AssemblyDefinition.ReadAssembly(path,
                new ReaderParameters { AssemblyResolver = _resolver, InMemory = true, ReadSymbols = false });
        }
        public void Dispose() { Assembly.Dispose(); _resolver.Dispose(); }
    }

    private static ConsumerRead ReadConsumer() => new(AssemblyPath);

    [Fact]
    public void ConsumerIdentityAndOptionalApiDependencyAreThePinnedArrivalSnapShape()
    {
        using var consumer = ReadConsumer();
        var assembly = consumer.Assembly;
        Assert.Equal("VGEcho", assembly.Name.Name);
        Assert.Equal("0.7.0.0", assembly.Name.Version.ToString());
        var plugin = assembly.MainModule.GetType(Plugin) ?? throw new InvalidOperationException("Missing VGEcho.Plugin.");
        // SOFT, not hard: the same build must load with the API absent, which the MissingApi
        // control exercises natively.
        var dependency = Assert.Single(plugin.CustomAttributes, attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInDependency"
            && attribute.ConstructorArguments.Count == 2
            && (string?)attribute.ConstructorArguments[0].Value == "vgmodapi");
        Assert.Equal(2, Convert.ToInt32(dependency.ConstructorArguments[1].Value)); // DependencyFlags.SoftDependency
        var version = Assert.Single(plugin.Fields, field => field.Name == "PluginVersion" && field.HasConstant);
        Assert.Equal("0.7.0", (string?)version.Constant);
    }

    [Fact]
    public void EveryConsumerMemberTheProbeReflectsHasItsDeclaredShape()
    {
        using var consumer = ReadConsumer();
        var module = consumer.Assembly.MainModule;
        TypeDefinition Type(string name) => module.GetType(name) ?? throw new InvalidOperationException("Missing type: " + name);

        // The timer write the probe observes, and the gate read behind it.
        var apply = Assert.Single(Type(TimingPatches).Methods, method => method.Name == "ApplyArrivalSnap");
        Assert.True(apply.IsStatic);
        Assert.Equal("System.Void", apply.ReturnType.FullName);
        Assert.Empty(apply.Parameters);
        Assert.Single(Type(TimingPatches).Methods, method => method.Name == "ReadArrivalSnapGates");

        // The subscription handle and the production binder the controlled reordering invokes.
        var observer = Assert.Single(Type(Plugin).Fields, field => field.Name == "_arrivalSnap");
        Assert.Equal("VGEcho.Travel.IArrivalSnapObserver", observer.FieldType.FullName);
        var bind = Assert.Single(Type(Plugin).Methods, method => method.Name == "BindArrivalSnap");
        Assert.True(!bind.IsStatic && bind.Parameters.Count == 0 && bind.ReturnType.FullName == "System.Void");
        Assert.Single(Type("VGEcho.Travel.IArrivalSnapObserver").Properties, property => property.Name == "IsListening"
            && property.PropertyType.FullName == "System.Boolean");
        Assert.Contains("System.IDisposable", Type("VGEcho.Travel.IArrivalSnapObserver").Interfaces
            .Select(entry => entry.InterfaceType.FullName));

        // The three configuration gates the sandbox pins.
        foreach (var entry in new[] { "CfgAutopilotTiming", "CfgAutopilotArrivalSnap", "CfgAutopilotEtaSync" })
            Assert.Single(Type(Plugin).Fields, field => field.Name == entry
                && field.FieldType.FullName == "BepInEx.Configuration.ConfigEntry`1<System.Boolean>");
    }

    [Fact]
    public void TheRetiredTimingHookIsAbsentWhileEchosUnrelatedAutomationKeepsItsOwn()
    {
        using var consumer = ReadConsumer();
        var module = consumer.Assembly.MainModule;
        var refusals = new List<string>();
        var declared = new List<string>();
        foreach (var type in AllTypes(module))
            foreach (var target in PatchTargets(type))
            {
                declared.Add(type.FullName + " -> " + target.Type + "." + target.Method);
                var refusal = EchoTravelReceipt.RefuseEchoTimingPatch(type.FullName, target.Type, target.Method);
                if (refusal != null) refusals.Add(refusal);
            }
        Assert.Empty(refusals);
        // The exact timing hook, spelled out: the timing patch class declares nothing on the native
        // route boundary at all.
        Assert.DoesNotContain(declared, entry => entry.StartsWith(TimingPatches, StringComparison.Ordinal)
            && entry.Contains(RouteBoundary, StringComparison.Ordinal));
        // ... and the claim is not vacuous: the class still owns its ETA-sync postfix.
        Assert.Contains(declared, entry => entry.StartsWith(TimingPatches, StringComparison.Ordinal)
            && entry.Contains("IdleManager", StringComparison.Ordinal) && entry.Contains("Update", StringComparison.Ordinal));
        // Echo's unrelated automation may legitimately own a patch on the same native boundary; the
        // probe must never assert that Echo owns NO native travel hook at all.
        Assert.All(declared.Where(entry => entry.Contains(RouteBoundary, StringComparison.Ordinal)),
            entry => Assert.DoesNotContain(TimingPatches, entry));
    }

    [Fact]
    public void TheTimerWriteIsReachableOnlyFromTheApiObserverPath()
    {
        using var consumer = ReadConsumer();
        var module = consumer.Assembly.MainModule;
        // The timer write is referenced from exactly ONE place: the binder that hands it to the API
        // observer as a delegate. No Harmony patch and no native path inside the consumer reaches it.
        Assert.Equal(new[] { "VGEcho.Plugin.BindArrivalSnap" }, Callers(module, TimingPatches, "ApplyArrivalSnap").ToArray());
        // The observer is the only type that turns an API fact into that call, and it is created by
        // the guarded bridge.
        Assert.Contains(Observer, Calls(module, "VGEcho.Travel.TravelArrivalBridge", "Subscribe")
            .Select(name => name).Concat(new[] { module.GetType(Observer)?.FullName ?? "" }));
        Assert.Contains("Receive", module.GetType(Observer)!.Methods.Select(method => method.Name));
    }

    [Fact]
    public void NoAlwaysJitPluginMemberNamesAnApiType()
    {
        using var consumer = ReadConsumer();
        var module = consumer.Assembly.MainModule;
        var plugin = module.GetType(Plugin) ?? throw new InvalidOperationException("Missing VGEcho.Plugin.");
        bool NamesApi(TypeReference? reference) => reference?.FullName.StartsWith("VGModAPI.", StringComparison.Ordinal) == true;
        foreach (var field in plugin.Fields) Assert.False(NamesApi(field.FieldType), "Plugin field names an API type: " + field.Name);
        foreach (var method in plugin.Methods)
        {
            Assert.False(NamesApi(method.ReturnType), "Plugin method returns an API type: " + method.Name);
            foreach (var parameter in method.Parameters)
                Assert.False(NamesApi(parameter.ParameterType), "Plugin method takes an API type: " + method.Name);
        }
        // The two guarded doorway methods are the only API-naming members, and they are no-inline
        // so a missing API costs exactly the one feature.
        var bridge = module.GetType("VGEcho.Travel.TravelArrivalBridge") ?? throw new InvalidOperationException("Missing bridge.");
        Assert.All(bridge.Methods.Where(method => method.Name is "Admit" or "Subscribe"),
            method => Assert.True(method.NoInlining, method.Name + " must stay NoInlining."));
    }

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

    /// <summary>Declared Harmony patch targets of a patch class: the class-level and method-level attributes.</summary>
    private static IEnumerable<(string Type, string Method)> PatchTargets(TypeDefinition type)
    {
        var attributes = type.CustomAttributes.Concat(type.Methods.SelectMany(method => method.CustomAttributes));
        foreach (var attribute in attributes.Where(candidate => candidate.AttributeType.Name == "HarmonyPatch"))
        {
            var target = attribute.ConstructorArguments.FirstOrDefault(argument => argument.Value is TypeReference).Value as TypeReference;
            var method = attribute.ConstructorArguments.FirstOrDefault(argument => argument.Value is string).Value as string;
            if (target != null || method != null) yield return (target?.FullName ?? "", method ?? "");
        }
    }

    private static HashSet<string> Calls(ModuleDefinition module, string owner, string method)
    {
        var type = AllTypes(module).Single(candidate => candidate.FullName == owner);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var instruction in type.Methods.Single(candidate => candidate.Name == method).Body.Instructions)
            if (instruction.Operand is MethodReference call) result.Add(call.Name);
        return result;
    }

    private static IEnumerable<string> Callers(ModuleDefinition module, string declaringType, string member)
    {
        var result = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods.Where(candidate => candidate.HasBody))
                if (method.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call
                    && call.Name == member && call.DeclaringType.FullName == declaringType))
                    result.Add(type.FullName + "." + method.Name);
        return result;
    }
}
