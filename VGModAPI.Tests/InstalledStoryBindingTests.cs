using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Runtime;
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

    /// <summary>
    /// The dependencies a mission MUST carry to survive the game's own save. sourceFaction.identifier
    /// is read unconditionally, so a mission without a source faction throws while the player saves;
    /// sourcePoi and turnIn are null-tolerant. This is why the definition requires a faction and why
    /// the adapter sets one.
    /// </summary>
    [Fact]
    public void SavingAMissionDereferencesItsSourceFactionUnconditionally()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var toJson = Method(module, Mission, "ToJson");
        var instructions = toJson.Body.Instructions.ToArray();
        int faction = Array.FindIndex(instructions, instruction =>
            instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "sourceFaction");
        Assert.True(faction >= 0);
        // Load the field, then call get_identifier on it, with no null branch in between.
        var window = instructions.Skip(faction).Take(5).ToArray();
        Assert.Contains(window, instruction => instruction.Operand is FieldReference field && field.Name == "sourceFaction");
        Assert.Contains(window, instruction => instruction.Operand is MethodReference method && method.Name == "get_identifier");
        Assert.DoesNotContain(window, instruction => instruction.OpCode == OpCodes.Brtrue || instruction.OpCode == OpCodes.Brtrue_S);
        // The POI fields ARE null-tolerant, which is why a source location is optional.
        int poi = Array.FindIndex(instructions, instruction =>
            instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == "sourcePoi");
        Assert.Contains(instructions.Skip(poi).Take(6), instruction => instruction.OpCode == OpCodes.Brtrue_S || instruction.OpCode == OpCodes.Brtrue);
        // Loading resolves the faction back through the game's own registry.
        Assert.Contains(Calls(Method(module, Mission, "DataFromJson")), name => name == "Get");
        var factionType = module.GetType("Source.Galaxy.Faction")!;
        var get = Assert.Single(factionType.Methods, method => method.Name == "Get" && method.Parameters.Count == 1);
        Assert.True(get.IsStatic);
        Assert.Equal("Source.Galaxy.Faction", get.ReturnType.FullName);
        Assert.Contains(factionType.Properties, property => property.Name == "identifier");
    }

    /// <summary>
    /// Why KillEnemies is refused: the objective serializes its enemy faction's identifier, so an
    /// objective without one breaks the save exactly as a missing source faction does.
    /// </summary>
    [Fact]
    public void TheRefusedObjectiveKindDependsOnAFactionThisSubsetCannotSupply()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var kill = module.GetType(StoryContentPolicy.ObjectiveNamespace + ".KillEnemies")!;
        Assert.Contains(kill.Fields, field => field.Name == "enemyFaction" && field.FieldType.FullName == "Source.Galaxy.Faction");
        var data = kill.Methods.Single(method => method.Name == "DataToJson");
        Assert.Contains(Calls(data), name => name == "get_identifier");
        Assert.NotNull(StoryContentPolicy.RefuseObjective(StoryObjectiveKind.KillEnemies));
        Assert.Null(StoryContentPolicy.RefuseObjective(StoryObjectiveKind.TravelToPoi));
        Assert.Null(StoryContentPolicy.RefuseObjective(StoryObjectiveKind.CollectCredits));
    }

    /// <summary>
    /// The load path a saved mission actually takes: the player's missions are full OBJECTS, and the
    /// catalog lookup only happens for a string element, which the game never writes. A missing
    /// provider therefore does not throw on load, which is why owned orphans need their own policy.
    /// </summary>
    [Fact]
    public void SavedMissionsAreFullObjectsSoAMissingProviderDoesNotStopTheLoad()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var toJson = Method(module, "Source.Player.GamePlayer", "ToJson");
        // The player writes its missions as an array built from Mission.ToJson, never as identifiers.
        Assert.Contains(Strings(toJson), text => text == "missions");
        Assert.Contains(Calls(toJson), name => name == "ToJsonArray");
        var fromJson = Method(module, Mission, "FromJson");
        Assert.Contains(Calls(fromJson), name => name == "get_IsString");
        Assert.Contains(Calls(fromJson), name => name == "Get");
        Assert.Contains(Calls(fromJson), name => name == "DataFromJson");
    }

    /// <summary>
    /// Every way a mission the game is holding can advance or pay out, which is what the quarantine
    /// guards sit in front of. A restored owned mission runs through these whether or not the module
    /// that owns it is present, so each one is bound by declared shape and refused for an orphan.
    /// </summary>
    [Fact]
    public void EveryGuardedProgressionEntryPointHasTheShapeTheGuardsBind()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var creditBalance = Assert.Single(module.GetType("Source.Player.GamePlayer")!.Properties, property => property.Name == "credits");
        Assert.Equal("System.Int64", creditBalance.PropertyType.FullName);
        var creditObjective = module.GetType("Source.MissionSystem.Objectives.CollectCredits")!;
        Assert.Equal("System.Int32", Assert.Single(creditObjective.Fields, field => field.Name == "requiredAmount").FieldType.FullName);
        var travelObjective = module.GetType("Source.MissionSystem.Objectives.TravelToPOI")!;
        Assert.Equal("System.String", Assert.Single(travelObjective.Fields, field => field.Name == "targetPOI").FieldType.FullName);
        Assert.Equal("System.Single", Assert.Single(travelObjective.Fields, field => field.Name == "requiredVisitTime").FieldType.FullName);
        Assert.Equal("System.Boolean", Assert.Single(travelObjective.Methods, method => method.Name == "IsComplete").ReturnType.FullName);
        var mission = module.GetType(Mission)!;
        var update = Assert.Single(mission.Methods, method => method.Name == "Update" && method.Parameters.Count == 1);
        Assert.Equal("System.Single", update.Parameters[0].ParameterType.FullName);
        // Per-frame progression completes the mission itself when it can be claimed.
        Assert.Contains(Calls(update), name => name == "CanClaimRewards");
        Assert.Contains(Calls(update), name => name == "CompleteMission");
        var claim = Assert.Single(mission.Methods, method => method.Name == "ClaimRewards");
        Assert.True(claim.IsVirtual);
        Assert.Equal("System.Boolean", claim.Parameters[0].ParameterType.FullName);
        var retry = Assert.Single(mission.Methods, method => method.Name == "RetryAsNextMission");
        // Retry resolves the story catalog, which is what throws for an orphan whose entry is gone.
        Assert.Contains(Calls(retry), name => name == "Get");
        var failed = Assert.Single(mission.Methods, method => method.Name == "MissionFailed");
        Assert.Contains(Calls(failed), name => name == "RetryAsNextMission");
        var complete = Assert.Single(module.GetType("Source.Player.GamePlayer")!.Methods,
            method => method.Name == "CompleteMission" && method.Parameters.Count == 2
                && method.Parameters[0].ParameterType.FullName == Mission);
        Assert.Contains(Calls(complete), name => name == "ClaimRewards");

        // Objective progression uses the base virtual dispatch plus the scripted override; both require guards.
        var objective = module.GetType(Objective)!;
        var trigger = Assert.Single(objective.Methods,
            method => method.Name == "ProcessMissionTrigger" && !method.IsStatic);
        Assert.True(trigger.IsVirtual);
        Assert.Equal(2, trigger.Parameters.Count);
        Assert.Equal("Source.MissionSystem.MissionTrigger", trigger.Parameters[0].ParameterType.FullName);
        Assert.Equal("System.Object", trigger.Parameters[1].ParameterType.FullName);
        var dispatch = Assert.Single(objective.Methods, method => method.Name == "Trigger" && method.IsStatic);
        Assert.Contains(Calls(dispatch), name => name == "ProcessMissionTrigger");
        Assert.Contains(Calls(dispatch), name => name == "get_allMissions");
        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
        {
            if (StoryContentPolicy.RefuseObjective(kind) != null) continue;
            var type = module.GetType(StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind))!;
            if (kind == StoryObjectiveKind.Scripted)
            {
                var scripted = Assert.Single(type.Methods, method => method.Name == "ProcessMissionTrigger");
                Assert.True(scripted.IsVirtual);
                Assert.Equal("System.Void", scripted.ReturnType.FullName);
                Assert.Equal(new[] { "Source.MissionSystem.MissionTrigger", "System.Object" }, scripted.Parameters.Select(value => value.ParameterType.FullName));
                Assert.Contains(BindingCatalog.StoryProtection, binding => binding.Key == "storyGuardScriptedTrigger");
            }
            else Assert.DoesNotContain(type.Methods, method => method.Name == "ProcessMissionTrigger");
        }
    }

    /// <summary>
    /// The route the game's abandon/retry button actually takes, which is the one the guard wraps.
    /// The button shows a confirmation whose callback calls AbandonMission; that method removes the
    /// mission and, for a retryable story mission, re-adds `nextMissionOnFailed ?? storyId` out of the
    /// catalog — a lookup that throws for an absent entry. The private RetryAsNextMission is a
    /// different, follow-up-only route, which API content never carries.
    /// </summary>
    [Fact]
    public void TheAbandonAndRetryButtonRemovesThenReAddsTheSameStoryIdentifier()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var details = module.GetType("Behaviour.UI.Missions.MissionDetails")
            ?? throw new InvalidOperationException("Missing MissionDetails");
        var abandon = Assert.Single(details.Methods, method => method.Name == "AbandonMission" && method.Parameters.Count == 1);
        Assert.Equal(Mission, abandon.Parameters[0].ParameterType.FullName);
        Assert.Contains(Fields(abandon), name => name == "nextMissionOnFailed");
        Assert.Contains(Fields(abandon), name => name == "storyId");
        Assert.Contains(Calls(abandon), name => name == "RemoveMission");
        Assert.Contains(Calls(abandon), name => name == "Get");                 // StoryMission.Get
        Assert.Contains(Calls(abandon), name => name == "AddMissionWithLog");
        Assert.Contains(Calls(abandon), name => name == "IsRetryableStoryMission");
        // The button itself only asks; the callback is what runs the route above.
        var button = Assert.Single(details.Methods, method => method.Name == "ButtonAbandon");
        Assert.Contains(Calls(button), name => name == "ShowQuery");
        var callback = Assert.Single(details.Methods, method => method.Name.Contains("ButtonAbandon", StringComparison.Ordinal)
            && method.Name != "ButtonAbandon");
        Assert.Contains(Calls(callback), name => name == "AbandonMission");
        // The private follow-up route is real but only runs for a mission carrying a follow-up id.
        var retry = Assert.Single(module.GetType(Mission)!.Methods, method => method.Name == "RetryAsNextMission");
        Assert.True(retry.IsPrivate);
        var failed = Assert.Single(module.GetType(Mission)!.Methods, method => method.Name == "MissionFailed");
        Assert.Contains(Fields(failed), name => name == "nextMissionOnFailed");
    }

    /// <summary>
    /// The capacity check the game makes for itself, and does NOT make on the route this API uses.
    /// AcceptMission asks IsMissionsLimitExceeded; AddMissionWithLog only checks duplicates. That is
    /// why the adapter asks the game's own method before handing a mission over.
    /// </summary>
    [Fact]
    public void OnlyTheGamesOwnAcceptRouteChecksCapacitySoTheAdapterMustAskItself()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var player = assembly.MainModule.GetType("Source.Player.GamePlayer")!;
        var limit = Assert.Single(player.Methods, method => method.Name == "IsMissionsLimitExceeded");
        Assert.Equal("System.Boolean", limit.ReturnType.FullName);
        Assert.Empty(limit.Parameters);
        var constant = Assert.Single(player.Fields, field => field.Name == "MissionLimit");
        Assert.True(constant.IsStatic);
        Assert.Equal("System.Int32", constant.FieldType.FullName);
        Assert.True(constant.HasConstant);
        // The check compares the held count against that same constant, which is why the adapter asks
        // the METHOD rather than hard-coding a number of its own.
        Assert.Contains(limit.Body.Instructions, instruction =>
            instruction.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(instruction.Operand) == Convert.ToInt32(constant.Constant));
        Assert.Contains(Calls(limit), name => name == "get_Count");
        var accept = Assert.Single(player.Methods, method => method.Name == "AcceptMission");
        Assert.Contains(Calls(accept), name => name == "IsMissionsLimitExceeded");
        var add = player.Methods.Single(method => method.Name == "AddMissionWithLog" && method.Parameters.Count == 2);
        Assert.DoesNotContain(Calls(add), name => name == "IsMissionsLimitExceeded");
        Assert.Contains(Calls(add), name => name == "HasStoryMission");
    }

    /// <summary>
    /// The game's faction lookup never returns null: it resolves a TYPE by identifier, constructs it
    /// and registers it, and throws for anything else. That is why the API resolves the type itself
    /// before asking, and why faction identities are those PascalCase type names.
    /// </summary>
    [Fact]
    public void TheFactionLookupCreatesFromATypeNameAndThrowsForAnythingElse()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath);
        var module = assembly.MainModule;
        var faction = module.GetType("Source.Galaxy.Faction")!;
        var get = Assert.Single(faction.Methods, method => method.Name == "Get" && method.Parameters.Count == 1);
        Assert.Contains(Calls(get), name => name == "TryGetValue");
        Assert.Contains(Calls(get), name => name == "Create");
        Assert.Contains(Calls(get), name => name == "set_Item");          // it registers what it creates
        var create = Assert.Single(faction.Methods, method => method.Name == "Create");
        Assert.Contains(Strings(create), text => text == StoryNativeBindings.FactionNamespace + ".");
        Assert.Contains(Calls(create), name => name == "GetType");
        Assert.Contains(Calls(create), name => name == "GetConstructor");
        // The identities the API may name are exactly those types.
        var known = module.Types.Where(type => type.Namespace == StoryNativeBindings.FactionNamespace
            && type.BaseType?.FullName == "Source.Galaxy.Faction").Select(type => type.Name).ToArray();
        Assert.Contains("TradingGuild", known);
        Assert.DoesNotContain("tradingGuild", known);
        // And the galaxy answers whether a travel target exists at all.
        var galaxy = module.GetType("Source.Galaxy.GalaxyMapData")!;
        Assert.Contains(galaxy.Properties, property => property.Name == "current");
        Assert.Contains(galaxy.Methods, method => method.Name == "GetPointOfInterest" && method.Parameters.Count == 1
            && method.Parameters[0].ParameterType.FullName == "System.String");
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
