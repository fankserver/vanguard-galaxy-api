using System;
using System.Threading;

namespace VGModAPI;

/// <summary>
/// API-constructed foundational service references. Objects remain stable across session replacement;
/// their availability and domain state do not. Obtain the live root after the API plugin's Awake.
/// </summary>
public sealed class ModServices
{
    private readonly int _thread = Thread.CurrentThread.ManagedThreadId;
    private readonly ILifecycleService _lifecycle;
    private readonly IModInformationService _mods;
    private readonly IModSettingsService _settings;
    private readonly ISaveDataService _saveData;
    private readonly IMissionService _missions;
    private readonly ITravelService _travel;
    private readonly IStationService _station;
    private readonly IRecipeService _recipes;
    private readonly IRecipeQuoteService _recipeQuotes;
    private readonly ICraftingJobService _craftingJobs;
    private readonly ICraftingCommandService _craftingCommands;
    private readonly IHudService _hud;
    private readonly IEquipmentService _equipment;
    private readonly ISkillTreeService _skillTrees;
    private readonly ITooltipService _tooltips;
    private readonly IPickupPresentationService _pickupPresentation;
    public IEquipmentService Equipment { get { CheckThread(); return _equipment; } }
    public ISkillTreeService SkillTrees { get { CheckThread(); return _skillTrees; } }
    public ITooltipService Tooltips { get { CheckThread(); return _tooltips; } }
    public IPickupPresentationService PickupPresentation { get { CheckThread(); return _pickupPresentation; } }
    private readonly IGameplayUiService _gameplayUi;
    private readonly IForgeUiService _forgeUi;
    private readonly IBoardingRuleService _boardingRules;
    private readonly IDungeonService _dungeons;
    private readonly IStoryService _story;
    private readonly IBarService _bars;
    private readonly IWorldService _world;
    private readonly IOwnedRecipeService _recipeRegistration;
    public IOwnedRecipeService RecipeRegistration { get { CheckThread(); return _recipeRegistration; } }
    private readonly IOwnedItemService _items;
    public IOwnedItemService Items { get { CheckThread(); return _items; } }
    private readonly IGameService _game;
    public IGameService Game { get { CheckThread(); return _game; } }
    private readonly IDialogueService _dialogue;
    public IDialogueService Dialogue { get { CheckThread(); return _dialogue; } }
    public IWorldService World { get { CheckThread(); return _world; } }
    public ILifecycleService Lifecycle { get { CheckThread(); return _lifecycle; } }
    public IModInformationService Mods { get { CheckThread(); return _mods; } }
    public IModSettingsService Settings { get { CheckThread(); return _settings; } }
    public ISaveDataService SaveData { get { CheckThread(); return _saveData; } }
    public IMissionService Missions { get { CheckThread(); return _missions; } }
    public ITravelService Travel { get { CheckThread(); return _travel; } }
    public IStationService Station { get { CheckThread(); return _station; } }
    public IRecipeService Recipes { get { CheckThread(); return _recipes; } }
    public IRecipeQuoteService RecipeQuotes { get { CheckThread(); return _recipeQuotes; } }
    public ICraftingJobService CraftingJobs { get { CheckThread(); return _craftingJobs; } }
    public ICraftingCommandService CraftingCommands { get { CheckThread(); return _craftingCommands; } }

    public IHudService Hud { get { CheckThread(); return _hud; } }
    public IGameplayUiService GameplayUi { get { CheckThread(); return _gameplayUi; } }

    public IForgeUiService ForgeUi { get { CheckThread(); return _forgeUi; } }

    public IBoardingRuleService BoardingRules { get { CheckThread(); return _boardingRules; } }

    public IDungeonService Dungeons { get { CheckThread(); return _dungeons; } }

    public IStoryService Story { get { CheckThread(); return _story; } }

    public IBarService Bars { get { CheckThread(); return _bars; } }

    internal ModServices(ILifecycleService lifecycle, IModInformationService mods, IModSettingsService settings, ISaveDataService saveData,
        IMissionService missions, ITravelService travel, IStationService station, IRecipeService recipes, IRecipeQuoteService recipeQuotes,
        ICraftingJobService craftingJobs, ICraftingCommandService craftingCommands, IHudService hud, IForgeUiService forgeUi, IBoardingRuleService boardingRules, IDungeonService dungeons, IStoryService story, IBarService bars, IWorldService world, IDialogueService dialogue, IGameService game, IOwnedItemService items, IOwnedRecipeService recipeRegistration, IGameplayUiService gameplayUi, IEquipmentService equipment, ISkillTreeService skillTrees, ITooltipService tooltips, IPickupPresentationService pickupPresentation)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _saveData = saveData ?? throw new ArgumentNullException(nameof(saveData));
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _travel = travel ?? throw new ArgumentNullException(nameof(travel));
        _station = station ?? throw new ArgumentNullException(nameof(station));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _recipeQuotes = recipeQuotes ?? throw new ArgumentNullException(nameof(recipeQuotes));
        _craftingJobs = craftingJobs ?? throw new ArgumentNullException(nameof(craftingJobs));
        _craftingCommands = craftingCommands ?? throw new ArgumentNullException(nameof(craftingCommands));
        _hud = hud ?? throw new ArgumentNullException(nameof(hud));
        _equipment = equipment ?? throw new ArgumentNullException(nameof(equipment));
        _skillTrees = skillTrees ?? throw new ArgumentNullException(nameof(skillTrees));
        _tooltips = tooltips ?? throw new ArgumentNullException(nameof(tooltips));
        _pickupPresentation = pickupPresentation ?? throw new ArgumentNullException(nameof(pickupPresentation));
        _gameplayUi = gameplayUi ?? throw new ArgumentNullException(nameof(gameplayUi));
        _forgeUi = forgeUi ?? throw new ArgumentNullException(nameof(forgeUi));
        _boardingRules = boardingRules ?? throw new ArgumentNullException(nameof(boardingRules));
        _dungeons = dungeons ?? throw new ArgumentNullException(nameof(dungeons));
        _story = story ?? throw new ArgumentNullException(nameof(story));
        _bars = bars ?? throw new ArgumentNullException(nameof(bars));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _recipeRegistration = recipeRegistration ?? throw new ArgumentNullException(nameof(recipeRegistration));
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI service access requires the Unity main thread.");
    }
}
