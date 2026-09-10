using System;
using System.Linq;
using System.Reflection;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

[Trait("Category", "InstalledGame")]
public sealed class InstalledAuthoredSiteRuntimeResolutionTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    /// <summary>
    /// The authored-site seam builds native Unity value types (position, cargo size) through
    /// reflection. The OLD code resolved UnityEngine.Vector2 with a by-name lookup against the game
    /// assembly, which threw at runtime (Vector2 lives in UnityEngine.CoreModule, not Assembly-CSharp)
    /// and disabled the whole authored chain. The fix resolves Vector2 from GetWorldPosition's return
    /// type, which reflection spells correctly. This test reproduces the real runtime reflection
    /// against the installed assembly so a Cecil metadata test alone cannot miss a regression.
    /// </summary>
    [Fact]
    public void AuthoredSiteVector2ResolvesFromGetWorldPositionNotByNameLookup()
    {
        var game = Assembly.LoadFrom(AssemblyPath);

        // The defect: the game assembly does not itself define UnityEngine.Vector2, so a by-name
        // lookup throws. Assert the fix's premise rather than depending on thrown-vs-null, which would
        // change across runtimes.
        Assert.Null(game.GetType("UnityEngine.Vector2", throwOnError: false));

        // The fix: GetWorldPosition's declared return type resolves to the real UnityEngine.Vector2
        // (defined in UnityEngine.CoreModule) with the canonical shape the native calls expect.
        var poi = game.GetType(AuthoredSiteBindings.Poi, true)!;
        var member = poi.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == "GetWorldPosition" && m.GetParameters().Length == 0);
        var vector = member.ReturnType;
        Assert.Equal("UnityEngine.Vector2", vector.FullName);
        Assert.Equal("UnityEngine.CoreModule", vector.Assembly.GetName().Name);
        Assert.Equal("UnityEngine.Vector2", NativeTypeName.Canonical(vector));

        // The native shape used by the seam's survivors (fields x/y) must be present on the resolved
        // type. (Value types like Vector2 are constructable via Activator.CreateInstance regardless of
        // whether a parameterless ctor is explicitly declared, so no ctor assertion is made.)
        Assert.NotNull(vector.GetField("x", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(vector.GetField("y", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }
}
