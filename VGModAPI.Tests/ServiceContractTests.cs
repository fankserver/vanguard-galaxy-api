using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Xunit;

namespace VGModAPI.Tests;

public sealed class ServiceContractTests
{
    [Fact]
    public void AvailabilityHasTypedReasonsAndValueEquality()
    {
        foreach (var reason in Enum.GetValues<ServiceUnavailableReason>())
        {
            var state = new ServiceAvailability(reason, "detail");
            Assert.Equal(reason == ServiceUnavailableReason.None, state.IsAvailable);
            Assert.Equal(state, new ServiceAvailability(reason, "detail"));
            Assert.Equal(state.GetHashCode(), new ServiceAvailability(reason, "detail").GetHashCode());
            Assert.NotEqual(state, new ServiceAvailability(reason, "different"));
        }
        Assert.True(ServiceAvailability.Available.IsAvailable);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceAvailability((ServiceUnavailableReason)999));
        Assert.Throws<ArgumentNullException>(() => new ServiceAvailability(ServiceUnavailableReason.None, null!));
    }

    [Fact]
    public void RootHasOnlyNonNullableReadOnlyServicesAndNoPublicConstruction()
    {
        Assert.True(typeof(ModServices).IsSealed);
        Assert.Empty(typeof(ModServices).GetConstructors());
        var nullable = new NullabilityInfoContext();
        var properties = typeof(ModServices).GetProperties();
        Assert.NotEmpty(properties);
        foreach (var property in properties)
        {
            Assert.Equal(NullabilityState.NotNull, nullable.Create(property).ReadState);
            Assert.Null(property.SetMethod);
            Assert.True(property.PropertyType.IsInterface);
        }
        // Composition contracts must not advertise a live accessor before the runtime supplies it.
        Assert.Null(typeof(ModApi).GetProperty("Services"));
    }

    [Fact]
    public void RootRejectsEveryMissingServiceWithoutCallingServiceCode()
    {
        var constructor = typeof(ModServices).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var parameters = constructor.GetParameters();
        var arguments = parameters.Select(p => DispatchProxy.Create(p.ParameterType, typeof(ConstructionProxy))).ToArray();
        var root = constructor.Invoke(arguments);
        for (var index = 0; index < parameters.Length; index++)
        {
            var property = typeof(ModServices).GetProperty(parameters[index].Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)!;
            Assert.Same(arguments[index], property.GetValue(root));
            var missing = (object?[])arguments.Clone();
            missing[index] = null;
            var error = Assert.Throws<TargetInvocationException>(() => constructor.Invoke(missing));
            Assert.Equal(parameters[index].Name, Assert.IsType<ArgumentNullException>(error.InnerException).ParamName);
        }
    }

    public class ConstructionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new InvalidOperationException("Root construction must not execute service code.");
    }

    [Theory]
    [InlineData(SaveDataStateKind.Ready)]
    [InlineData(SaveDataStateKind.Restoring)]
    public void RestoredOrRestoringStateRequiresSession(SaveDataStateKind kind)
    {
        Assert.Throws<ArgumentException>(() => new SaveDataState(kind));
        Assert.Equal(kind, new SaveDataState(kind, Guid.NewGuid()).Kind);
    }

    [Fact]
    public void SaveStateRejectsContradictoryOrUnknownStates()
    {
        var session = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Inactive, session));
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Disposed, session));
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Ready, Guid.Empty));
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Ready, session, SaveDataBlockReason.RestoreFailed));
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Blocked, session));
        Assert.Throws<ArgumentException>(() => new SaveDataState(SaveDataStateKind.Blocked, reason: SaveDataBlockReason.RestoreFailed));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveDataState((SaveDataStateKind)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveDataState(SaveDataStateKind.Blocked, session, (SaveDataBlockReason)999));
        Assert.Throws<ArgumentNullException>(() => new SaveDataState(SaveDataStateKind.Inactive, detail: null!));
        Assert.Null(new SaveDataState(SaveDataStateKind.Blocked, reason: SaveDataBlockReason.ServiceUnavailable).SessionId);
        foreach (var reason in Enum.GetValues<SaveDataBlockReason>().Where(r => r != SaveDataBlockReason.None))
            Assert.Equal(reason, new SaveDataState(SaveDataStateKind.Blocked, session, reason).Reason);
    }

    [Fact]
    public void RegistrationRefusalCannotPretendToHaveAHandle()
    {
        var registration = new FakeSaveRegistration();
        foreach (var status in Enum.GetValues<SaveDataRegistrationStatus>())
        {
            if (status == SaveDataRegistrationStatus.Registered)
            {
                Assert.True(new SaveDataRegistrationResult(status, registration).Succeeded);
                Assert.Throws<ArgumentException>(() => new SaveDataRegistrationResult(status));
            }
            else
            {
                Assert.False(new SaveDataRegistrationResult(status).Succeeded);
                Assert.Throws<ArgumentException>(() => new SaveDataRegistrationResult(status, registration));
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaveDataRegistrationResult((SaveDataRegistrationStatus)999));
        Assert.Throws<ArgumentNullException>(() => new SaveDataRegistrationResult(SaveDataRegistrationStatus.Unavailable, detail: null!));
    }

    [Fact]
    public void InventoryPreservesFreshnessAndCopiesRows()
    {
        var a = Row("a"); var b = Row("b");
        var rows = new[] { b, a };
        var snapshot = new ModInventorySnapshot(ModInventoryStatus.Current, rows);
        rows[0] = a;
        Assert.Equal(new[] { "a", "b" }, snapshot.Entries.Select(row => row.PluginId));
        Assert.Equal(ModInventoryStatus.RefreshFailed, new ModInventorySnapshot(ModInventoryStatus.RefreshFailed, snapshot.Entries).Status);
        Assert.Equal(ModInventoryStatus.Partial, new ModInventorySnapshot(ModInventoryStatus.Partial, snapshot.Entries).Status);
        Assert.Throws<ArgumentException>(() => new ModInventorySnapshot(ModInventoryStatus.NotCollected, snapshot.Entries));
        Assert.Throws<ArgumentException>(() => new ModInventorySnapshot(ModInventoryStatus.Current, rows));
        Assert.Throws<ArgumentException>(() => new ModInventorySnapshot(ModInventoryStatus.Current, new ModInformation[] { null! }));
        Assert.Throws<ArgumentNullException>(() => new ModInventorySnapshot(ModInventoryStatus.Current, null!));
        Assert.Throws<ArgumentNullException>(() => new ModInventorySnapshot(ModInventoryStatus.NotCollected, Array.Empty<ModInformation>(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ModInventorySnapshot((ModInventoryStatus)999, Array.Empty<ModInformation>()));
    }

    private static ModInformation Row(string id) => new(id, id, new Version(1, 0), Array.Empty<ModDependencyInformation>(), null, ModMetadataStatus.Missing);

    [Fact, Trait("Category", "BinaryInspection")]
    public void SupportedPublicMembersRemainAndPublishedInterfacesDoNotGainRequirements()
    {
        var expected = Baseline();
        Assert.Contains(expected, row => row.StartsWith("T\tVGModAPI.ModApi\t", StringComparison.Ordinal));
        Assert.Empty(Violations(expected, PublicContractShape.Read(typeof(ModApi).Assembly.Location)));
    }

    [Theory, Trait("Category", "BinaryInspection")]
    [InlineData("interface", true)]
    [InlineData("return-type", true)]
    [InlineData("class-addition", false)]
    public void CompatibilityCheckDetectsBreakingChangesButAllowsClassAdditions(string change, bool breaking)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(ModApi).Assembly.Location);
        var module = assembly.MainModule;
        if (change == "return-type")
            module.GetType("VGModAPI.ModApi").Methods.Single(m => m.Name == "get_Current").ReturnType = module.TypeSystem.Object;
        else
        {
            var flags = Mono.Cecil.MethodAttributes.Public;
            flags |= change == "interface" ? Mono.Cecil.MethodAttributes.Abstract | Mono.Cecil.MethodAttributes.Virtual | Mono.Cecil.MethodAttributes.NewSlot : Mono.Cecil.MethodAttributes.Static;
            var method = new MethodDefinition("AdditionalMember", flags, module.TypeSystem.Void);
            module.GetType(change == "interface" ? "VGModAPI.ILifecycleApi" : "VGModAPI.ModApi").Methods.Add(method);
            if (change != "interface") method.Body.GetILProcessor().Emit(Mono.Cecil.Cil.OpCodes.Ret);
        }
        var path = Path.Combine(Path.GetTempPath(), "vg-contract-" + Guid.NewGuid().ToString("N") + ".dll");
        try
        {
            assembly.Write(path);
            Assert.Equal(breaking, Violations(Baseline(), PublicContractShape.Read(path)).Length != 0);
        }
        finally { File.Delete(path); }
    }

    private static string[] Baseline()
    {
        using var stream = typeof(ServiceContractTests).Assembly.GetManifestResourceStream("VGModAPI.Tests.Contracts.SupportedPublicApi.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(row => row.TrimEnd('\r')).ToArray();
    }

    private static string[] Violations(string[] expected, string[] actual)
    {
        var interfaces = expected.Where(row => row.StartsWith("T\t", StringComparison.Ordinal) && row.Split('\t')[2] == "interface")
            .Select(row => row.Split('\t')[1]).ToHashSet(StringComparer.Ordinal);
        return expected.Except(actual, StringComparer.Ordinal).Select(row => "Missing/changed: " + row)
            .Concat(actual.Except(expected, StringComparer.Ordinal).Where(row => interfaces.Contains(row.Split('\t')[1]))
                .Select(row => "Published interface changed: " + row)).ToArray();
    }
}
