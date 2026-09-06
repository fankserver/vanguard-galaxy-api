using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Source.Player;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// REAL reflection resolution of the shipped binding catalogs against source-faithful doubles in
/// this assembly. The installed-assembly tests read metadata with Cecil, whose type-name spelling
/// already matches the catalog, so they cannot see a runtime reflection mismatch: qa-77 failed at
/// InstallTravel with MissingMethodException on TravelManager.CancelTravel while every metadata test
/// passed. These tests exercise <see cref="GameBindings.Resolve"/> itself.
/// This is host evidence about NAME MATCHING only; it is not native qualification.
/// </summary>
public sealed class GameBindingsResolutionTests
{
    private static readonly Assembly Fake = typeof(GamePlayer).Assembly;
    private static GameBindings Bindings() => new(Fake);

    [Fact]
    public void WholeTravelCatalogResolvesThroughRealReflection()
    {
        var resolved = Bindings().Resolve(BindingCatalog.Travel);
        Assert.Equal(BindingCatalog.Travel.Length, resolved.Count);
        foreach (var binding in BindingCatalog.Travel)
        {
            var method = resolved[binding.Key];
            Assert.Equal(binding.Name, method.Name);
            Assert.Equal(binding.Type, method.DeclaringType!.FullName);
            Assert.Equal(binding.Static, method.IsStatic);
            Assert.Equal(binding.ReturnType, NativeTypeName.Canonical(method.ReturnType));
            Assert.Equal(binding.Parameters, method.GetParameters().Select(p => NativeTypeName.Canonical(p.ParameterType)).ToArray());
        }
    }

    // The exact qa-77 failure: the catalog declares the metadata spelling of the constructed
    // generic, reflection spells it assembly-qualified, so a raw FullName comparison never matched.
    [Fact]
    public void ConstructedGenericParameterResolvesAndItsRawFullNameStillDiffers()
    {
        var cancel = Assert.Single(BindingCatalog.Travel, b => b.Key == "cancel");
        Assert.Equal("System.Nullable`1<UnityEngine.Vector2>", Assert.Single(cancel.Parameters));
        var native = Fake.GetType(cancel.Type, true)!.GetMethod(cancel.Name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        var parameter = Assert.Single(native.GetParameters()).ParameterType;
        Assert.NotEqual(cancel.Parameters[0], parameter.FullName);           // the defect that shipped
        Assert.Contains("[[UnityEngine.Vector2,", parameter.FullName);        // assembly-qualified argument
        Assert.Equal(cancel.Parameters[0], NativeTypeName.Canonical(parameter));
        Assert.Same(native, Bindings().Resolve(new[] { cancel })[cancel.Key]);
    }

    [Fact]
    public void MalformedOrWrongGenericArgumentsAreRejected()
    {
        foreach (var declared in new[]
        {
            "System.Nullable`1<UnityEngine.Vector3>",                        // wrong argument
            "System.Nullable`1<UnityEngine.Vector2, UnityEngine.Vector2>",   // wrong arity spelling
            "System.Nullable`1[[UnityEngine.Vector2, VGModAPI.Tests]]",      // assembly-qualified
            "System.Nullable`1",                                             // open definition
            "UnityEngine.Vector2",                                           // unwrapped argument
            "System.Nullable`1<UnityEngine.Vector2>[]",                      // array of the argument
            "System.Nullable`1<UnityEngine.Vector2>&"                        // by-ref
        })
        {
            var binding = new MethodBinding("cancel", BindingCatalog.TravelManager, "CancelTravel", false, "System.Boolean", declared);
            Assert.Throws<MissingMethodException>(() => Bindings().Resolve(new[] { binding }));
        }
    }

    [Fact]
    public void ExactShapeConstraintsAreNotWeakened()
    {
        var cancel = Assert.Single(BindingCatalog.Travel, b => b.Key == "cancel");
        foreach (var wrong in new[]
        {
            new MethodBinding("k", cancel.Type, cancel.Name, true, cancel.ReturnType, cancel.Parameters),         // static
            new MethodBinding("k", cancel.Type, cancel.Name, false, "System.Void", cancel.Parameters),            // return type
            new MethodBinding("k", cancel.Type, cancel.Name, false, cancel.ReturnType),                           // arity
            new MethodBinding("k", cancel.Type, "CancelTravelling", false, cancel.ReturnType, cancel.Parameters), // name
            new MethodBinding("k", BindingCatalog.DockingOption, cancel.Name, false, cancel.ReturnType, cancel.Parameters) // owner
        })
        {
            Assert.ThrowsAny<Exception>(() => Bindings().Resolve(new[] { wrong }));
        }
    }

    // Canonicalisation normalises the SHAPE only; every decoration stays discriminating.
    [Theory]
    [InlineData(typeof(int), "System.Int32")]
    [InlineData(typeof(string[]), "System.String[]")]
    [InlineData(typeof(int[,]), "System.Int32[,]")]
    [InlineData(typeof(IEnumerator), "System.Collections.IEnumerator")]
    [InlineData(typeof(UnityEngine.Vector2?), "System.Nullable`1<UnityEngine.Vector2>")]
    [InlineData(typeof(System.Collections.Generic.List<UnityEngine.Vector2?>), "System.Collections.Generic.List`1<System.Nullable`1<UnityEngine.Vector2>>")]
    [InlineData(typeof(System.Collections.Generic.Dictionary<string, int>), "System.Collections.Generic.Dictionary`2<System.String,System.Int32>")]
    [InlineData(typeof(System.Collections.Generic.List<int>[]), "System.Collections.Generic.List`1<System.Int32>[]")]
    public void CanonicalNamesAreAssemblyFreeAndShapePreserving(Type type, string expected)
        => Assert.Equal(expected, NativeTypeName.Canonical(type));

    [Fact]
    public void ByRefAndPointerDecorationSurvives()
    {
        Assert.Equal("System.Int32&", NativeTypeName.Canonical(typeof(int).MakeByRefType()));
        Assert.Equal("System.Int32*", NativeTypeName.Canonical(typeof(int).MakePointerType()));
        Assert.Equal("System.Nullable`1<UnityEngine.Vector2>&", NativeTypeName.Canonical(typeof(UnityEngine.Vector2?).MakeByRefType()));
        Assert.Equal("System.Nullable`1<UnityEngine.Vector2>[]", NativeTypeName.Canonical(typeof(UnityEngine.Vector2?).MakeArrayType()));
    }

    // Every declared catalog name must be in the canonical spelling, so a future binding cannot
    // reintroduce a shape that reflection can never match.
    [Fact]
    public void EveryCatalogTypeNameIsCanonical()
    {
        var declared = BindingCatalog.Session.Concat(BindingCatalog.Saves).Concat(BindingCatalog.Missions)
            .Concat(BindingCatalog.MissionSnapshots).Concat(BindingCatalog.Travel)
            .SelectMany(b => new[] { b.ReturnType }.Concat(b.Parameters)).Distinct().ToArray();
        foreach (var name in declared)
        {
            Assert.DoesNotContain("[[", name);                    // assembly-qualified argument list
            Assert.DoesNotContain(", Version=", name);
            Assert.DoesNotContain("+", name);                     // nested types would need a spelling decision
            if (name.Contains('<') || name.Contains('`'))
                Assert.Matches(@"^[\w.]+`\d+<[\w.`,<>]+>$", name);
        }
    }
}
