using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using VGModAPI;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>
/// Exact declared shapes of the native story surface, resolved once against the inspected assembly.
/// Every member is required: a missing or changed shape throws here, before anything is installed, so
/// the story capability stays unavailable instead of half-bound. No member is used for anything but
/// the operations <see cref="StoryNativeWorld"/> performs.
/// </summary>
internal sealed class StoryNativeBindings
{
    /// <summary>Where the game resolves a faction identity from; its identifiers ARE these type names.</summary>
    internal const string FactionNamespace = "Source.Galaxy.Factions";

    private readonly Type _storyMission, _createMission, _mission, _missionStep, _objective, _reward, _difficulty, _player, _faction, _galaxy;
    private readonly FieldInfo _allMissions, _storyIdentifier, _missionStoryId, _missionName, _missionDescription,
        _missionCategory, _missionCompletionText, _missionDifficulty, _missionCanAbandon,
        _missionSourceFaction, _missionSourcePoi, _missionSourceName, _missionIconName, _missionDynamicLevel, _missionTrackedOnHud,
        _stepDescription, _stepRequireAll, _playerCurrent, _playerMissions, _playerArchive, _playerPoi, _allFactions;
    private readonly PropertyInfo _missionSteps, _missionRewards, _stepObjectives, _galaxyCurrent;
    private readonly MethodInfo _storyAdd, _storyGet, _objectiveCreate, _rewardCreate, _addMission, _hasStory, _activeStory, _removeMission, _factionGet, _galaxyPoi;
    private readonly ConstructorInfo _storyCtor, _missionCtor, _stepCtor;

    internal StoryNativeBindings(Assembly assembly)
    {
        if (assembly == null) throw new ArgumentNullException(nameof(assembly));
        _storyMission = Type(assembly, "Source.MissionSystem.StoryMission");
        _createMission = _storyMission.GetNestedType("CreateMission", BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(_storyMission.FullName, "CreateMission");
        _mission = Type(assembly, BindingCatalog.Mission);
        _missionStep = Type(assembly, "Source.MissionSystem.MissionStep");
        _objective = Type(assembly, "Source.MissionSystem.MissionObjective");
        _reward = Type(assembly, "Source.MissionSystem.MissionReward");
        _difficulty = Type(assembly, "Source.MissionSystem.MissionDifficulty");
        _player = Type(assembly, BindingCatalog.Player);
        _faction = Type(assembly, "Source.Galaxy.Faction");

        _allMissions = Field(_storyMission, "allMissions");
        if (_allMissions.FieldType != typeof(Dictionary<,>).MakeGenericType(typeof(string), _storyMission))
            throw new MissingFieldException(_storyMission.FullName, "allMissions");
        _storyIdentifier = Field(_storyMission, "identifier");
        _storyCtor = _storyMission.GetConstructors().Single(constructor => constructor.GetParameters().Length == 4);
        var parameters = _storyCtor.GetParameters();
        if (parameters[0].ParameterType != typeof(string) || parameters[1].ParameterType != _createMission
            || parameters[3].ParameterType != typeof(string))
            throw new MissingMethodException(_storyMission.FullName, ".ctor");
        _storyAdd = Method(_storyMission, "Add", new[] { _storyMission });
        _storyGet = Method(_storyMission, "Get", new[] { _player, typeof(string) });
        if (_storyGet.ReturnType != _mission) throw new MissingMethodException(_storyMission.FullName, "Get");

        _missionCtor = _mission.GetConstructor(System.Type.EmptyTypes) ?? throw new MissingMethodException(_mission.FullName, ".ctor");
        _missionStoryId = Field(_mission, "storyId");
        _missionName = Field(_mission, "name");
        _missionDescription = Field(_mission, "description");
        _missionCategory = Field(_mission, "category");
        _missionCompletionText = Field(_mission, "completionText");
        _missionDifficulty = Field(_mission, "difficulty");
        _missionCanAbandon = Field(_mission, "canAbandon");
        // The game writes sourceFaction.identifier unconditionally when it saves a held mission, so the
        // faction is REQUIRED; sourcePoi, turnIn and the text fields are null-tolerant there.
        _missionSourceFaction = Field(_mission, "sourceFaction");
        if (_missionSourceFaction.FieldType != _faction) throw new MissingFieldException(_mission.FullName, "sourceFaction");
        _missionSourcePoi = Field(_mission, "sourcePoi");
        _missionSourceName = Field(_mission, "sourceName");
        _missionIconName = Field(_mission, "iconName");
        _missionDynamicLevel = Field(_mission, "dynamicLevel");
        _missionTrackedOnHud = Field(_mission, "trackedOnHud");
        _factionGet = Method(_faction, "Get", new[] { typeof(string) });
        _allFactions = Field(_faction, "allFactions");
        _galaxy = Type(assembly, "Source.Galaxy.GalaxyMapData");
        _galaxyCurrent = _galaxy.GetProperty("current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMemberException(_galaxy.FullName, "current");
        _galaxyPoi = Method(_galaxy, "GetPointOfInterest", new[] { typeof(string) });
        if (_factionGet.ReturnType != _faction || !_factionGet.IsStatic) throw new MissingMethodException(_faction.FullName, "Get");
        _missionSteps = Property(_mission, "steps");
        _missionRewards = Property(_mission, "rewards");

        _stepCtor = _missionStep.GetConstructor(System.Type.EmptyTypes) ?? throw new MissingMethodException(_missionStep.FullName, ".ctor");
        _stepDescription = Field(_missionStep, "description");
        _stepRequireAll = Field(_missionStep, "requireAllObjectives");
        _stepObjectives = Property(_missionStep, "objectives");

        _objectiveCreate = Method(_objective, "Create", new[] { typeof(string) });
        _rewardCreate = Method(_reward, "Create", new[] { typeof(string) });

        _playerCurrent = Field(_player, "current");
        _playerMissions = Field(_player, "missions");
        _playerArchive = Field(_player, "missionsArchive");
        _playerPoi = Field(_player, "currentPointOfInterest");
        if (_playerPoi.FieldType != _missionSourcePoi.FieldType) throw new MissingFieldException(_player.FullName, "currentPointOfInterest");
        // force:false keeps vanilla's own duplicate-story guard, which is the refusal this API relies on.
        _addMission = Method(_player, "AddMissionWithLog", new[] { _mission, typeof(bool) });
        _hasStory = Method(_player, "HasStoryMission", new[] { typeof(string) });
        _activeStory = Method(_player, "GetActiveStoryMission", new[] { typeof(string) });
        // completed:false abandons without archiving, so the API never claims a completion vanilla did not make.
        _removeMission = Method(_player, "RemoveMission", new[] { _mission, typeof(bool) });

        foreach (StoryObjectiveKind kind in Enum.GetValues(typeof(StoryObjectiveKind)))
            RequireDerived(assembly, StoryContentPolicy.ObjectiveNamespace + "." + StoryContentPolicy.ObjectiveTypeName(kind), _objective);
        foreach (StoryRewardKind kind in Enum.GetValues(typeof(StoryRewardKind)))
            RequireDerived(assembly, StoryContentPolicy.RewardNamespace + "." + StoryContentPolicy.RewardTypeName(kind), _reward);
        foreach (StoryDifficulty difficulty in Enum.GetValues(typeof(StoryDifficulty)))
            if (!Enum.IsDefined(_difficulty, Enum.Parse(_difficulty, StoryContentPolicy.DifficultyName(difficulty))))
                throw new MissingFieldException(_difficulty.FullName, StoryContentPolicy.DifficultyName(difficulty));
    }

    private static Type Type(Assembly assembly, string name) => assembly.GetType(name, true)!;
    private static FieldInfo Field(Type type, string name) => type.GetField(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        ?? throw new MissingFieldException(type.FullName, name);
    private static PropertyInfo Property(Type type, string name) => type.GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        ?? throw new MissingMemberException(type.FullName, name);
    private static MethodInfo Method(Type type, string name, Type[] parameters) => type.GetMethod(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly,
        null, parameters, null) ?? throw new MissingMethodException(type.FullName, name);
    private static void RequireDerived(Assembly assembly, string name, Type expectedBase)
    {
        var type = Type(assembly, name);
        if (type.IsAbstract || !expectedBase.IsAssignableFrom(type) || type.GetConstructor(System.Type.EmptyTypes) == null)
            throw new MissingMemberException(name, ".ctor");
    }

    internal object? CurrentPlayer => _playerCurrent.GetValue(null);
    /// <summary>
    /// Whether the game already knows this faction, or could create it from its own factions. The
    /// game's lookup NEVER returns null: it resolves <c>Source.Galaxy.Factions.&lt;identifier&gt;</c> and
    /// constructs it, throwing for anything else and REGISTERING whatever it constructs. So the
    /// question is answered by resolving the type here, without calling the lookup and without adding
    /// anything to the game's registry.
    /// </summary>
    internal bool KnowsFaction(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return false;
        if (((IDictionary)_allFactions.GetValue(null)!).Contains(identifier)) return true;
        var type = _faction.Assembly.GetType(FactionNamespace + "." + identifier, false);
        return type != null && !type.IsAbstract && _faction.IsAssignableFrom(type) && type.GetConstructor(System.Type.EmptyTypes) != null;
    }

    /// <summary>Resolves a faction that <see cref="KnowsFaction"/> has already accepted.</summary>
    internal object? Faction(string identifier) => _factionGet.Invoke(null, new object[] { identifier });
    internal IDictionary Catalog => (IDictionary)_allMissions.GetValue(null)!;
    internal string CatalogIdentifier(object definition) => (string?)_storyIdentifier.GetValue(definition) ?? "";

    /// <summary>
    /// Builds the vanilla definition object with a generator that calls back into this API. The
    /// delegate is created from an expression tree rather than a saved provider callback: nothing of
    /// the consumer is captured, and nothing of it is ever persisted.
    /// </summary>
    internal object CreateDefinition(string identifier, Func<object, object> generator, string pickupHint)
    {
        var player = Expression.Parameter(_player, "player");
        var invoke = Expression.Call(Expression.Constant(generator), generator.GetType().GetMethod("Invoke")!,
            Expression.Convert(player, typeof(object)));
        var typed = Expression.Convert(invoke, _mission);
        var native = Expression.Lambda(_createMission, typed, player).Compile();
        return _storyCtor.Invoke(new object?[] { identifier, native, null, pickupHint });
    }

    internal void AddDefinition(object definition) => _storyAdd.Invoke(null, new[] { definition });
    internal object BuildMission(object player, string identifier) => _storyGet.Invoke(null, new[] { player, (object)identifier })!;

    /// <summary>Builds the mission body from a definition using vanilla's own factories only.</summary>
    internal object CreateMission(StoryMissionDefinition definition, string identifier, object? player)
    {
        var faction = Faction(definition.SourceFaction.Value)
            ?? throw new InvalidOperationException("The game does not know faction '" + definition.SourceFaction + "'.");
        var mission = _missionCtor.Invoke(null);
        // Every vanilla generator sets these; without a source faction the game cannot even save.
        _missionSourceFaction.SetValue(mission, faction);
        _missionSourceName.SetValue(mission, "");
        _missionIconName.SetValue(mission, "");
        _missionDynamicLevel.SetValue(mission, true);
        _missionTrackedOnHud.SetValue(mission, false);
        // The location the mission is taken FROM, which is where the player is when it is accepted.
        // The game tolerates a null source POI on save; it is never invented from elsewhere.
        if (player != null) _missionSourcePoi.SetValue(mission, _playerPoi.GetValue(player));
        _missionName.SetValue(mission, definition.Title);
        _missionDescription.SetValue(mission, definition.Description);
        _missionCategory.SetValue(mission, definition.Category ?? "");
        _missionCompletionText.SetValue(mission, definition.CompletionText ?? "");
        _missionDifficulty.SetValue(mission, Enum.Parse(_difficulty, StoryContentPolicy.DifficultyName(definition.Difficulty)));
        _missionCanAbandon.SetValue(mission, definition.CanAbandon);
        _missionStoryId.SetValue(mission, identifier);
        var steps = (IList)_missionSteps.GetValue(mission)!;
        foreach (var step in definition.Steps)
        {
            var native = _stepCtor.Invoke(null);
            _stepDescription.SetValue(native, step.Description);
            _stepRequireAll.SetValue(native, step.RequireAllObjectives);
            var objectives = (IList)_stepObjectives.GetValue(native)!;
            foreach (var objective in step.Objectives) objectives.Add(CreateObjective(objective));
            steps.Add(native);
        }
        var rewards = (IList)_missionRewards.GetValue(mission)!;
        foreach (var reward in definition.Rewards) rewards.Add(CreateReward(reward));
        return mission;
    }

    private object CreateObjective(StoryObjective objective)
    {
        var name = StoryContentPolicy.ObjectiveTypeName(objective.Kind);
        var native = _objectiveCreate.Invoke(null, new object[] { name })
            ?? throw new InvalidOperationException("Vanilla did not create objective '" + name + "'.");
        switch (objective.Kind)
        {
            case StoryObjectiveKind.TravelToPoi:
                Field(native.GetType(), "targetPOI").SetValue(native, objective.TargetPoiId);
                Field(native.GetType(), "requiredVisitTime").SetValue(native, objective.RequiredVisitSeconds);
                break;
            case StoryObjectiveKind.KillEnemies:
            case StoryObjectiveKind.CollectCredits:
                Field(native.GetType(), "requiredAmount").SetValue(native, objective.RequiredAmount);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(objective));
        }
        return native;
    }

    private object CreateReward(StoryReward reward)
    {
        var name = StoryContentPolicy.RewardTypeName(reward.Kind);
        var native = _rewardCreate.Invoke(null, new object[] { name })
            ?? throw new InvalidOperationException("Vanilla did not create reward '" + name + "'.");
        Field(native.GetType(), "amount").SetValue(native, reward.Amount);
        Field(native.GetType(), "baseAmount").SetValue(native, reward.Amount);
        return native;
    }

    /// <summary>
    /// Whether the loaded galaxy holds this point of interest. A travel objective aimed at a guid the
    /// world does not have is a mission that can never be completed, so it is refused rather than
    /// offered.
    /// </summary>
    internal bool? KnowsPointOfInterest(string guid)
    {
        var galaxy = _galaxyCurrent.GetValue(null);
        if (galaxy == null) return null;                 // No galaxy loaded: nothing can be asserted.
        return _galaxyPoi.Invoke(galaxy, new object[] { guid }) != null;
    }

    internal void Accept(object player, object mission) => _addMission.Invoke(player, new[] { mission, (object)false });
    internal bool HasStory(object player, string identifier) => (bool)_hasStory.Invoke(player, new object[] { identifier })!;
    internal object? ActiveStory(object player, string identifier) => _activeStory.Invoke(player, new object[] { identifier });
    internal void Abandon(object player, object mission) => _removeMission.Invoke(player, new[] { mission, (object)false });
    internal string?[] ActiveStoryIdentifiers(object player)
        => ((IEnumerable)_playerMissions.GetValue(player)!).Cast<object>().Select(mission => (string?)_missionStoryId.GetValue(mission)).ToArray();
    internal string[] ArchivedIdentifiers(object player)
        => ((IEnumerable)_playerArchive.GetValue(player)!).Cast<object>().Select(value => (string)value).ToArray();
}
