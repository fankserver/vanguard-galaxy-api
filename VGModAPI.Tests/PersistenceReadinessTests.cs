using System;
using System.Linq;
using System.Reflection;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Readiness is an ADDITIVE capability, like <see cref="ILifecycleDispatchState"/> was for the
/// lifecycle API: the shipped <see cref="IPersistenceRegistration"/> keeps exactly the members it has
/// had since 0.1.2, so a consumer that implements or wraps that handle keeps compiling and keeps
/// type-loading under Mono, and a consumer that wants readiness casts for it.
/// </summary>
public sealed class PersistenceReadinessTests
{
    /// <summary>An implementation written against the shipped interface, with no readiness member.</summary>
    private sealed class LegacyRegistration : IPersistenceRegistration
    {
        internal bool Disposed;
        public bool MutationAllowed => !Disposed;
        public string Status => Disposed ? "inactive" : "ready";
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void TheShippedRegistrationInterfaceKeepsExactlyItsPublishedShape()
    {
        var contract = typeof(IPersistenceRegistration);
        Assert.Equal(new[] { "MutationAllowed", "Status" },
            contract.GetProperties().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(contract.GetMethods(), method => !method.IsSpecialName);
        Assert.Equal(new[] { typeof(IDisposable) }, contract.GetInterfaces());
        // The added capability is a separate interface, not a member of the shipped one.
        Assert.DoesNotContain(contract.GetMembers(), member => member.Name.Contains("StateReady"));
        var capability = typeof(IPersistenceReadiness);
        Assert.Equal(new[] { "StateReady" }, capability.GetProperties().Select(property => property.Name).ToArray());
        Assert.Empty(capability.GetInterfaces());
        // No default interface implementation: those do not load on the runtime this targets.
        Assert.All(capability.GetMethods(), method => Assert.True(method.IsAbstract));
    }

    [Fact]
    public void AnImplementationWithoutTheCapabilityStillCompilesAndLoads()
    {
        // Constructing and using it IS the type-load check: a missing abstract member would throw
        // TypeLoadException here rather than at compile time.
        IPersistenceRegistration legacy = new LegacyRegistration();
        Assert.True(legacy.MutationAllowed);
        Assert.Equal("ready", legacy.Status);
        Assert.False(legacy is IPersistenceReadiness);
        legacy.Dispose();
        Assert.False(legacy.MutationAllowed);
        var declared = typeof(LegacyRegistration).GetInterfaceMap(typeof(IPersistenceRegistration));
        Assert.Equal(2, declared.InterfaceMethods.Count(method => method.IsSpecialName && method.ReturnType != typeof(void)));
    }

    /// <summary>The actual runtime producer implements the capability, so the story module is not gated out.</summary>
    [Fact]
    public void TheRuntimeRegistrationImplementsTheReadinessCapability()
    {
        var registration = typeof(PersistenceService).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => typeof(IPersistenceRegistration).IsAssignableFrom(type));
        Assert.True(typeof(IPersistenceReadiness).IsAssignableFrom(registration));
    }
}
