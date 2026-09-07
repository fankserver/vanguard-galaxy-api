using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Host regressions for the metadata SCAN the installed-consumer tests rely on. The scan carries a
/// real conclusion — "the consumer's visited-system map can only grow inside its public-event
/// observer" — so a scan that silently misses a call site would turn that proof into a false pass.
/// These run against a synthetic in-memory module, so they need no consumer binary.
/// </summary>
public sealed class AnimaConsumerMetadataScanTests
{
    private const string Member = "NoteSystemVisit";

    /// <summary>
    /// Outer -> Nested -> compiler-generated state machine, the shape a `foreach`/`async` body
    /// inside an already nested type produces. The call lives two levels down.
    /// </summary>
    private static AssemblyDefinition SyntheticDeeplyNestedCaller()
    {
        var assembly = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition("SyntheticConsumer", new Version(1, 0)), "SyntheticConsumer", ModuleKind.Dll);
        var module = assembly.MainModule;
        var registry = new TypeDefinition("Synthetic", "Registry",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        var noteVisit = new MethodDefinition(Member, MethodAttributes.Public, module.TypeSystem.Void);
        noteVisit.Body.GetILProcessor().Emit(OpCodes.Ret);
        registry.Methods.Add(noteVisit);
        module.Types.Add(registry);

        var outer = new TypeDefinition("Synthetic", "Observer",
            TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
        module.Types.Add(outer);
        var nested = new TypeDefinition(string.Empty, "Inner",
            TypeAttributes.NestedPublic | TypeAttributes.Class, module.TypeSystem.Object);
        outer.NestedTypes.Add(nested);
        var stateMachine = new TypeDefinition(string.Empty, "<Observe>d__7",
            TypeAttributes.NestedPrivate | TypeAttributes.Class, module.TypeSystem.Object);
        nested.NestedTypes.Add(stateMachine);
        var moveNext = new MethodDefinition("MoveNext", MethodAttributes.Public, module.TypeSystem.Void);
        var il = moveNext.Body.GetILProcessor();
        il.Emit(OpCodes.Call, noteVisit);
        il.Emit(OpCodes.Ret);
        stateMachine.Methods.Add(moveNext);
        return assembly;
    }

    [Fact]
    public void TheScanFindsACallHiddenTwoNestingLevelsDeepAndAttributesItToItsDeclaringMethod()
    {
        using var assembly = SyntheticDeeplyNestedCaller();
        var module = assembly.MainModule;
        Assert.Equal(new[] { "Synthetic.Observer/Inner.Observe" },
            InstalledAnimaTravelConsumerTests.Callers(module, Member).ToArray());
        Assert.Contains(InstalledAnimaTravelConsumerTests.AllTypes(module), type => type.Name == "<Observe>d__7");
    }

    [Fact]
    public void ASingleLevelWalkWouldMissThatCallSite()
    {
        using var assembly = SyntheticDeeplyNestedCaller();
        // The exact defect this scan must not have: one level of nesting hides the state machine,
        // so a "nothing else calls it" conclusion would pass without ever seeing the call.
        var oneLevel = assembly.MainModule.Types.SelectMany(type => new[] { type }.Concat(type.NestedTypes)).ToArray();
        Assert.DoesNotContain(oneLevel, type => type.Name == "<Observe>d__7");
        Assert.DoesNotContain(oneLevel.Where(type => type.HasMethods).SelectMany(type => type.Methods),
            method => method.HasBody && method.Body.Instructions.Any(instruction =>
                instruction.Operand is MethodReference call && call.Name == Member));
    }
}
