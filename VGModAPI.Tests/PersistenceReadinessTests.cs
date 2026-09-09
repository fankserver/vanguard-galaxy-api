using System;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class PersistenceReadinessTests
{
    [Fact]
    public void RegistrationRequiresTypedStateAndLiveReadWriteGates()
    {
        var contract = typeof(ISaveDataRegistration);
        Assert.Equal(new[] { "CanMutate", "CanRead", "State" }, contract.GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(typeof(SaveDataState), contract.GetProperty("State")!.PropertyType);
        Assert.NotNull(contract.GetEvent("StateChanged"));
        Assert.Contains(typeof(IDisposable), contract.GetInterfaces());
        Assert.All(contract.GetMethods(), method => Assert.True(method.IsAbstract));
    }

    [Fact]
    public void RuntimeImplementsTheTypedContractDirectly()
    {
        Assert.True(typeof(ISaveDataService).IsAssignableFrom(typeof(PersistenceService)));
        Assert.Single(typeof(PersistenceService).GetNestedTypes(BindingFlags.NonPublic), t => typeof(ISaveDataRegistration).IsAssignableFrom(t));
        foreach (var name in new[] { "IPersistenceApi", "IPersistenceRegistration", "IPersistenceReadiness" })
            Assert.Null(typeof(ModApi).Assembly.GetType("VGModAPI." + name));
        Assert.Null(typeof(ModApi).GetProperty("Persistence"));
    }
}
