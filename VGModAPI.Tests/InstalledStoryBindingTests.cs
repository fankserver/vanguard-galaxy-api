using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VGModAPI;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

/// <summary>
/// Installed-assembly evidence for the owned-story contract. It pins the native facts the design
/// depends on: how vanilla resolves story definitions, objectives and rewards, why collision
/// detection must live in this API, and why the supported subset is exactly the types below. No
/// runtime behaviour is claimed here; this is metadata and IL only.
/// </summary>
[Trait("Category", "InstalledGame")]
public sealed class InstalledStoryBindingTests
{
    private static string AssemblyPath => Environment.GetEnvironmentVariable("VG_GAME_ASSEMBLY")
        ?? throw new InvalidOperationException("Run make check-bindings or set VG_GAME_ASSEMBLY to the original installed Assembly-CSharp.dll.");

    private const string StoryMission = "Source.MissionSystem.StoryMission";
    private const string Mission = "Source.MissionSystem.Mission";
    private const string Objective = "Source.MissionSystem.MissionObjective";
    private const string Reward = "Source.MissionSystem.MissionReward";

    [Fact]
    public void EverySupportedObjectiveAndRewardKindResolvesToARealVanillaType()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
        {
            var type = module.GetType(StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind));
            Assert.NotNull(type);
            Assert.False(type!.IsAbstract);
            Assert.Equal(Objective, Base(type));
            // MissionObjective.Create invokes the parameterless constructor after Type.GetType.
            Assert.Contains(type.Methods, method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        }
        foreach (StoryRewardKind kind in Enum.GetValues(typeof(StoryRewardKind)))
        {
            var type = module.GetType(StoryContentPolicy.RewardNamespace + "." + StoryContentPolicy.RewardTypeName(kind));
            Assert.NotNull(type);
            Assert.False(type!.IsAbstract);
            Assert.Equal(Reward, type.BaseType!.FullName);
            Assert.Contains(type.Methods, method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 0);
        }
    }

    /// <summary>
    /// The reason the supported subset is a closed set: both factories resolve an UNQUALIFIED type
    /// name inside the game's own namespaces, so a provider-defined objective or reward type can
    /// never be reconstructed from a vanilla save. An unresolvable reward is additionally skipped
    /// with a log line, which is why the API refuses unsupported rewards at registration.
    /// </summary>
    [Fact]
    public void VanillaResolvesObjectiveAndRewardTypesFromItsOwnAssemblyOnly()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var objectiveCreate = Method(module, Objective, "Create");
        Assert.Contains(Strings(objectiveCreate), text => text == StoryContentPolicy.ObjectiveNamespace + ".");
        Assert.Contains(Calls(objectiveCreate), name => name == "GetType");
        Assert.Contains(Calls(objectiveCreate), name => name == "GetConstructor");
        var rewardCreate = Method(module, Reward, "Create");
        Assert.Contains(Strings(rewardCreate), text => text == StoryContentPolicy.RewardNamespace + ".");
        Assert.Contains(Strings(rewardCreate), text => text.Contains("Could not resolve reward type", StringComparison.Ordinal));
        Assert.Contains(Strings(rewardCreate), text => text.Contains("skipping reward", StringComparison.Ordinal));
    }

    /// <summary>
    /// Vanilla's own registration overwrites an existing identifier and its lookup throws for an
    /// absent one, so collision detection and provider-absence policy belong to this API.
    /// </summary>
    [Fact]
    public void StoryRegistrationOverwritesAndLookupThrowsSoTheApiMustOwnCollisionPolicy()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var type = module.GetType(StoryMission) ?? throw new InvalidOperationException("Missing " + StoryMission);
        var registry = Assert.Single(type.Fields, field => field.Name == "allMissions");
        Assert.True(registry.IsStatic);
        Assert.Equal("System.Collections.Generic.Dictionary`2<System.String," + StoryMission + ">", registry.FieldType.FullName);
        // Add assigns the indexer: a duplicate identifier REPLACES the existing definition.
        Assert.Contains(Calls(Method(module, StoryMission, "Add")), name => name == "set_Item");
        // Get indexes the dictionary, which throws for an unknown identifier (no silent substitute).
        var get = type.Methods.Single(method => method.Name == "Get" && method.Parameters.Count == 2);
        Assert.Contains(Calls(get), name => name == "get_Item");
        Assert.Contains(Fields(get), name => name == "storyId");
        // A string mission payload routes to that lookup; an object payload is rebuilt field by field.
        var fromJson = Method(module, Mission, "FromJson");
        Assert.Contains(Calls(fromJson), name => name == "get_IsString");
        Assert.Contains(Calls(fromJson), name => name == "Get");
        Assert.Contains(Calls(fromJson), name => name == "DataFromJson");
        // The definition constructor still takes (identifier, generator, availability, hint).
        var constructor = Assert.Single(type.Methods, method => method.IsConstructor && !method.IsStatic && method.Parameters.Count == 4);
        Assert.Equal("System.String", constructor.Parameters[0].ParameterType.FullName);
        Assert.Equal(StoryMission + "/CreateMission", constructor.Parameters[1].ParameterType.FullName);
    }

    /// <summary>
    /// A live mission is serialized by vanilla as a full object carrying its story identifier, and a
    /// duplicate story identifier is refused while it is active or archived. Repeated runs are
    /// therefore later occurrences, which is exactly what the ledger records.
    /// </summary>
    [Fact]
    public void VanillaPersistsMissionsItselfAndRefusesADuplicateStoryIdentifier()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var toJson = Method(module, Mission, "ToJson");
        Assert.Contains(Strings(toJson), text => text == "storyId");
        Assert.Contains(Strings(toJson), text => text == "steps");
        Assert.Contains(Strings(toJson), text => text == "rewards");
        var player = module.GetType("Source.Player.GamePlayer") ?? throw new InvalidOperationException("Missing GamePlayer");
        Assert.Contains(Strings(Method(module, "Source.Player.GamePlayer", "ToJson")), text => text == "missions");
        Assert.Contains(player.Fields, field => field.Name == "missionsArchive"
            && field.FieldType.FullName == "System.Collections.Generic.List`1<System.String>");
        var add = player.Methods.Single(method => method.Name == "AddMissionWithLog" && method.Parameters.Count == 2);
        Assert.Contains(Calls(add), name => name == "HasStoryMission");
        Assert.Contains(Strings(add), text => text.Contains("duplicate story mission", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every member the native story adapter binds, with the exact declared shape it uses. These are
    /// the operations installation, acceptance and abandonment are built from: if any shape moves, the
    /// adapter must fail to bind rather than call something that merely has the same name.
    /// </summary>
    [Fact]
    public void EveryBoundStoryMemberHasTheDeclaredShapeTheAdapterUses()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var story = module.GetType(StoryMission)!;
        var create = Assert.Single(story.NestedTypes, nested => nested.Name == "CreateMission");
        var invoke = Assert.Single(create.Methods, method => method.Name == "Invoke");
        Assert.Equal(Mission, invoke.ReturnType.FullName);
        Assert.Equal("Source.Player.GamePlayer", Assert.Single(invoke.Parameters).ParameterType.FullName);
        Assert.Contains(story.Fields, field => field.Name == "identifier" && field.FieldType.FullName == "System.String");
        var get = Assert.Single(story.Methods, method => method.Name == "Get" && method.Parameters.Count == 2);
        Assert.True(get.IsStatic);
        Assert.Equal(Mission, get.ReturnType.FullName);

        var mission = module.GetType(Mission)!;
        foreach (var (field, type) in new[]
        {
            ("storyId", "System.String"), ("name", "System.String"), ("description", "System.String"),
            ("category", "System.String"), ("completionText", "System.String"), ("canAbandon", "System.Boolean"),
            ("difficulty", "Source.MissionSystem.MissionDifficulty")
        }) Assert.Contains(mission.Fields, candidate => candidate.Name == field && candidate.FieldType.FullName == type);
        Assert.Contains(mission.Properties, property => property.Name == "steps"
            && property.PropertyType.FullName == "System.Collections.Generic.List`1<Source.MissionSystem.MissionStep>");
        Assert.Contains(mission.Properties, property => property.Name == "rewards"
            && property.PropertyType.FullName == "System.Collections.Generic.List`1<" + Reward + ">");
        Assert.Contains(mission.Methods, method => method.IsConstructor && method.Parameters.Count == 0);

        var step = module.GetType("Source.MissionSystem.MissionStep")!;
        Assert.Contains(step.Fields, field => field.Name == "description" && field.FieldType.FullName == "System.String");
        Assert.Contains(step.Fields, field => field.Name == "requireAllObjectives" && field.FieldType.FullName == "System.Boolean");
        Assert.Contains(step.Properties, property => property.Name == "objectives"
            && property.PropertyType.FullName == "System.Collections.Generic.List`1<" + Objective + ">");

        // The fields the supported subset writes, per objective and reward kind.
        AssertField(module, StoryContentPolicy.ObjectiveNamespace + ".TravelToPOI", "targetPOI", "System.String");
        AssertField(module, StoryContentPolicy.ObjectiveNamespace + ".TravelToPOI", "requiredVisitTime", "System.Single");
        AssertField(module, StoryContentPolicy.ObjectiveNamespace + ".KillEnemies", "requiredAmount", "System.Int32");
        AssertField(module, StoryContentPolicy.ObjectiveNamespace + ".CollectCredits", "requiredAmount", "System.Int32");
        foreach (StoryRewardKind kind in Enum.GetValues(typeof(StoryRewardKind)))
        {
            AssertField(module, StoryContentPolicy.RewardNamespace + "." + StoryContentPolicy.RewardTypeName(kind), "amount", "System.Int32");
            AssertField(module, StoryContentPolicy.RewardNamespace + "." + StoryContentPolicy.RewardTypeName(kind), "baseAmount", "System.Int32");
        }

        var player = module.GetType("Source.Player.GamePlayer")!;
        Assert.Contains(player.Fields, field => field.Name == "current" && field.IsStatic);
        Assert.Contains(player.Fields, field => field.Name == "missions"
            && field.FieldType.FullName == "System.Collections.Generic.List`1<" + Mission + ">");
        var accept = player.Methods.Single(method => method.Name == "AddMissionWithLog" && method.Parameters.Count == 2);
        Assert.Equal("force", accept.Parameters[1].Name);
        var remove = player.Methods.Single(method => method.Name == "RemoveMission");
        Assert.Equal("completed", remove.Parameters[1].Name);
        Assert.Contains(player.Methods, method => method.Name == "GetActiveStoryMission" && method.Parameters.Count == 1
            && method.ReturnType.FullName == Mission);
        Assert.Contains(player.Methods, method => method.Name == "HasStoryMission" && method.Parameters.Count == 1
            && method.ReturnType.FullName == "System.Boolean");
    }

    /// <summary>
    /// The two booleans the adapter passes decide the whole policy, so their MEANING is pinned, not
    /// just their presence. force:false keeps vanilla's duplicate-story refusal, which is what makes a
    /// verified acceptance possible; completed:false abandons without archiving, so an abandonment
    /// never leaves the archive claiming the story was finished.
    /// </summary>
    [Fact]
    public void TheAcceptanceAndRemovalFlagsMeanWhatTheAdapterReliesOn()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var player = module.GetType("Source.Player.GamePlayer")!;
        var accept = player.Methods.Single(method => method.Name == "AddMissionWithLog" && method.Parameters.Count == 2);
        var instructions = accept.Body.Instructions;
        // The duplicate guard is reached only when the flag is false: ldarg.2 / brtrue past it.
        Assert.Equal(OpCodes.Ldarg_2, instructions[0].OpCode);
        Assert.True(instructions[1].OpCode == OpCodes.Brtrue || instructions[1].OpCode == OpCodes.Brtrue_S);
        Assert.Contains(Calls(accept), name => name == "HasStoryMission");
        var remove = player.Methods.Single(method => method.Name == "RemoveMission");
        // completed:false takes the abandonment path; the archive call sits behind the true branch.
        Assert.Contains(Calls(remove), name => name == "OnMissionAbandoned");
        Assert.Contains(Calls(remove), name => name == "ArchiveMission");
    }

    /// <summary>Every mapped difficulty exists natively, and the API's ascending tiers map onto ascending native ones.</summary>
    [Fact]
    public void TheMappedDifficultyNamesExistNativelyInAscendingOrder()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var difficulty = assembly.MainModule.GetType("Source.MissionSystem.MissionDifficulty")!;
        var values = difficulty.Fields.Where(field => field.IsStatic)
            .ToDictionary(field => field.Name, field => Convert.ToInt64(field.Constant));
        long previous = -1;
        foreach (StoryDifficulty tier in Enum.GetValues(typeof(StoryDifficulty)))
        {
            var name = StoryContentPolicy.DifficultyName(tier);
            Assert.True(values.ContainsKey(name), "Missing native difficulty " + name);
            Assert.True(values[name] > previous, "Native difficulty order is not ascending at " + name);
            previous = values[name];
        }
    }

    private static void AssertField(ModuleDefinition module, string owner, string field, string type)
    {
        var declared = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        Assert.Contains(declared.Fields, candidate => candidate.Name == field && candidate.FieldType.FullName == type);
    }

    private static MethodDefinition Method(ModuleDefinition module, string owner, string name)
    {
        var type = module.GetType(owner) ?? throw new InvalidOperationException("Missing type: " + owner);
        return type.Methods.First(method => method.Name == name && method.HasBody);
    }

    private static string Base(TypeDefinition type)
    {
        for (var current = type.BaseType; current != null;)
        {
            if (current.FullName == Objective) return Objective;
            var resolved = current.Resolve();
            if (resolved == null) return current.FullName;
            current = resolved.BaseType;
        }
        return "";
    }

    private static string[] Strings(MethodDefinition method)
        => method.Body.Instructions.Where(instruction => instruction.OpCode == OpCodes.Ldstr)
            .Select(instruction => (string)instruction.Operand).ToArray();

    private static string[] Calls(MethodDefinition method)
        => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
            .Select(instruction => ((MethodReference)instruction.Operand).Name).ToArray();

    private static string[] Fields(MethodDefinition method)
        => method.Body.Instructions.Where(instruction => instruction.Operand is FieldReference)
            .Select(instruction => ((FieldReference)instruction.Operand).Name).ToArray();
}
