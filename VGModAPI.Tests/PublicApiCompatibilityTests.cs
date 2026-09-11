using System;
using System.Linq;
using System.Reflection;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Guards the shapes deployed plugins were compiled against. Adding a parameter to an existing public
/// constructor or interface method silently breaks every already-built consumer at runtime with a
/// MissingMethodException/TypeLoadException — which is exactly what happened in game when
/// PocketSystemDefinition gained a parameter. New options must arrive as an ADDITIONAL overload, and the
/// previous shape must stay callable.
/// </summary>
public sealed class PublicApiCompatibilityTests
{
    private static readonly Assembly Api = typeof(PocketSystemDefinition).Assembly;

    private static void AssertConstructor(string typeName, params Type[] parameters)
    {
        var type = Api.GetType("VGModAPI." + typeName, throwOnError: true)!;
        var found = type.GetConstructors().Any(c => c.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
        Assert.True(found, typeName + " must keep a (.ctor) overload with exactly: "
            + string.Join(", ", parameters.Select(p => p.Name)));
    }

    private static void AssertMethod(string typeName, string name, params Type[] parameters)
    {
        var type = Api.GetType("VGModAPI." + typeName, throwOnError: true)!;
        var method = type.GetMethods().SingleOrDefault(m => m.Name == name
            && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters));
        Assert.True(method != null, typeName + "." + name + " must keep an overload with exactly: "
            + string.Join(", ", parameters.Select(p => p.Name)));
    }

    [Fact]
    public void PocketSystemDefinitionKeepsItsPreSectorNameConstructor()
    {
        AssertConstructor("PocketSystemDefinition",
            typeof(string), typeof(int), typeof(string), typeof(PocketSystemPlacement), typeof(string));
        // The current shape is still available alongside it.
        AssertConstructor("PocketSystemDefinition",
            typeof(string), typeof(int), typeof(string), typeof(PocketSystemPlacement), typeof(string), typeof(string), typeof(bool));
    }

    [Fact]
    public void WormholePairDefinitionKeepsItsPreQuietConstructor()
    {
        AssertConstructor("WormholePairDefinition", typeof(string), typeof(int), typeof(string));
        AssertConstructor("WormholePairDefinition", typeof(string), typeof(int), typeof(string), typeof(bool));
    }

    [Fact]
    public void AmbientTrafficKeepsItsTwoArgumentSystemDeclaration()
    {
        AssertMethod("IAmbientTrafficService", "SuppressInSystemContaining", typeof(string), typeof(string));
        // The patrol-stripping form is an extra overload rather than a changed signature.
        AssertMethod("IAmbientTrafficService", "SuppressInSystemContaining", typeof(string), typeof(string), typeof(bool));
        AssertMethod("IAmbientTrafficService", "SuppressAtStation", typeof(string), typeof(string));
        AssertMethod("IAmbientTrafficService", "SuppressAtWormhole", typeof(string), typeof(string));
    }

    [Fact]
    public void WormholePairKeepItsDissolveAndQuerySurface()
    {
        AssertMethod("IWormholePair", "Dissolve");
        AssertMethod("IWormholePair", "SetOpen", typeof(bool));
    }

    [Fact]
    public void SettledMapBoundsExposeTheAuthoredPlacementBand()
    {
        Assert.Equal(-38f, SettledMapBounds.MinX);
        Assert.Equal(38f, SettledMapBounds.MaxX);
        Assert.Equal(-6f, SettledMapBounds.MinY);
        Assert.Equal(6f, SettledMapBounds.MaxY);
        Assert.True(SettledMapBounds.Contains(0f, 0f));
    }
}
