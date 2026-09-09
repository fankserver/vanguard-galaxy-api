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
        var accessor = typeof(ModApi).GetProperty(nameof(ModApi.Services))!;
        Assert.Equal(typeof(ModServices), accessor.PropertyType);
        Assert.Null(accessor.SetMethod);
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

    [Fact]
    public void InventoryHasNoSupersededPublicContractOrAccessor()
    {
        Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI.IModInformationCatalog"));
        Assert.Null(typeof(ModApi).GetProperty("Mods"));
        Assert.Null(typeof(Core.ModInformationCatalog).GetProperty("Snapshot"));
        Assert.True(typeof(IModInformationService).IsAssignableFrom(typeof(Core.ModInformationCatalog)));
    }
}
