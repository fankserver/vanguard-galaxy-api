using System;
using System.Linq;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests;

public sealed class TravelArrivalBindingsTests
{
    public abstract class Root { public virtual void SpaceshipHasArrived() { } }
    public class Inherited : Root { }
    public class Override : Root { public override void SpaceshipHasArrived() => base.SpaceshipHasArrived(); }
    public class Deep : Override { public override void SpaceshipHasArrived() => base.SpaceshipHasArrived(); }
    public abstract class AbstractOverride : Root { public override void SpaceshipHasArrived() => base.SpaceshipHasArrived(); }
    public class InheritedAbstract : AbstractOverride { }
    public class Hidden : Root { public new void SpaceshipHasArrived() { } }
    public class Overloaded : Root { public void SpaceshipHasArrived(bool unrelated) { } }
    public class Invalid { public void SpaceshipHasArrived() { } }

    [Fact]
    public void IncludesAbstractBaseAndDeclaredOverridesOnceInCalleeFirstOrder()
    {
        var methods = TravelArrivalBindings.Resolve(typeof(Root));
        Assert.Equal(typeof(Root), methods[0].DeclaringType);
        Assert.Equal(4, methods.Length);
        Assert.Equal(methods.Length, methods.Distinct().Count());
        Assert.Contains(methods, m => m.DeclaringType == typeof(AbstractOverride));
        Assert.True(Array.FindIndex(methods, m => m.DeclaringType == typeof(Override)) < Array.FindIndex(methods, m => m.DeclaringType == typeof(Deep)));
    }

    [Fact]
    public void InheritedImplementationsResolveToAlreadyCoveredDeclaration()
    {
        var methods = TravelArrivalBindings.Resolve(typeof(Root));
        foreach (var type in new[] { typeof(Inherited), typeof(InheritedAbstract), typeof(Overloaded) })
        {
            var inherited = type.GetMethod("SpaceshipHasArrived", Type.EmptyTypes)!;
            Assert.Contains(methods, method => method.Module == inherited.Module && method.MetadataToken == inherited.MetadataToken);
        }
        Assert.DoesNotContain(methods, m => m.DeclaringType == typeof(Hidden));
    }

    [Fact]
    public void RejectsNonvirtualLookalike()
    {
        Assert.Throws<MissingMethodException>(() => TravelArrivalBindings.Resolve(typeof(Invalid)));
    }
}
