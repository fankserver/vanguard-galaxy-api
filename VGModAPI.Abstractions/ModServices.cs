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
    private readonly ISaveDataService _saveData;
    private readonly IMissionService _missions;
    private readonly ITravelService _travel;
    private readonly IStationService _station;
    private readonly IRecipeService _recipes;
    private readonly IRecipeQuoteService _recipeQuotes;
    private readonly ICraftingJobService _craftingJobs;
    private readonly ICraftingCommandService _craftingCommands;
    private readonly IHudService _hud;
    private readonly IForgeUiService _forgeUi;
    private readonly IBoardingRuleService _boardingRules;
    private readonly IDungeonCombatService _dungeonCombat;
    private readonly IDungeonRewardService _dungeonRewards;
    private readonly IDungeonCommandService _dungeonCommands;
    private readonly IDungeonTacticalService _dungeonTactics;
    private readonly IDungeonOperationService _dungeonOperations;
    private readonly IDungeonSettlementService _dungeonSettlement;
    private readonly IDungeonPanelService _dungeonPanel;
    private readonly IDungeonContentService _dungeons;
    private readonly IStoryService _story;
    private readonly IBarService _bars;
    private readonly IWorldService _world;
    private readonly IInventoryService _inventories;
    public IInventoryService Inventories { get { CheckThread(); return _inventories; } }
    private readonly IOwnedRecipeService _recipeRegistration;
    public IOwnedRecipeService RecipeRegistration { get { CheckThread(); return _recipeRegistration; } }
    private readonly IOwnedItemService _items;
    public IOwnedItemService Items { get { CheckThread(); return _items; } }
    private readonly INavigationService _navigation;
    public INavigationService Navigation { get { CheckThread(); return _navigation; } }
    private readonly IDialogueService _dialogue;
    public IDialogueService Dialogue { get { CheckThread(); return _dialogue; } }
    public IWorldService World { get { CheckThread(); return _world; } }
    public ILifecycleService Lifecycle { get { CheckThread(); return _lifecycle; } }
    public IModInformationService Mods { get { CheckThread(); return _mods; } }
    public ISaveDataService SaveData { get { CheckThread(); return _saveData; } }
    public IMissionService Missions { get { CheckThread(); return _missions; } }
    public ITravelService Travel { get { CheckThread(); return _travel; } }
    public IStationService Station { get { CheckThread(); return _station; } }
    public IRecipeService Recipes { get { CheckThread(); return _recipes; } }
    public IRecipeQuoteService RecipeQuotes { get { CheckThread(); return _recipeQuotes; } }
    public ICraftingJobService CraftingJobs { get { CheckThread(); return _craftingJobs; } }
    public ICraftingCommandService CraftingCommands { get { CheckThread(); return _craftingCommands; } }

    public IHudService Hud { get { CheckThread(); return _hud; } }

    public IForgeUiService ForgeUi { get { CheckThread(); return _forgeUi; } }

    public IBoardingRuleService BoardingRules { get { CheckThread(); return _boardingRules; } }

    public IDungeonCombatService DungeonCombat { get { CheckThread(); return _dungeonCombat; } }
    public IDungeonRewardService DungeonRewards { get { CheckThread(); return _dungeonRewards; } }

    public IDungeonCommandService DungeonCommands { get { CheckThread(); return _dungeonCommands; } }

    public IDungeonTacticalService DungeonTactics { get { CheckThread(); return _dungeonTactics; } }

    public IDungeonOperationService DungeonOperations { get { CheckThread(); return _dungeonOperations; } }

    public IDungeonSettlementService DungeonSettlement { get { CheckThread(); return _dungeonSettlement; } }

    public IDungeonPanelService DungeonPanel { get { CheckThread(); return _dungeonPanel; } }

    public IDungeonContentService Dungeons { get { CheckThread(); return _dungeons; } }

    public IStoryService Story { get { CheckThread(); return _story; } }

    public IBarService Bars { get { CheckThread(); return _bars; } }

    internal ModServices(ILifecycleService lifecycle, IModInformationService mods, ISaveDataService saveData,
        IMissionService missions, ITravelService travel, IStationService station, IRecipeService recipes, IRecipeQuoteService recipeQuotes,
        ICraftingJobService craftingJobs, ICraftingCommandService craftingCommands, IHudService hud, IForgeUiService forgeUi, IBoardingRuleService boardingRules, IDungeonCombatService dungeonCombat, IDungeonRewardService dungeonRewards, IDungeonCommandService dungeonCommands, IDungeonTacticalService dungeonTactics, IDungeonOperationService dungeonOperations, IDungeonSettlementService dungeonSettlement, IDungeonPanelService dungeonPanel, IDungeonContentService dungeons, IStoryService story, IBarService bars, IWorldService world, IDialogueService dialogue, INavigationService navigation, IOwnedItemService items, IOwnedRecipeService recipeRegistration, IInventoryService inventories)
    {
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));
        _saveData = saveData ?? throw new ArgumentNullException(nameof(saveData));
        _missions = missions ?? throw new ArgumentNullException(nameof(missions));
        _travel = travel ?? throw new ArgumentNullException(nameof(travel));
        _station = station ?? throw new ArgumentNullException(nameof(station));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _recipeQuotes = recipeQuotes ?? throw new ArgumentNullException(nameof(recipeQuotes));
        _craftingJobs = craftingJobs ?? throw new ArgumentNullException(nameof(craftingJobs));
        _craftingCommands = craftingCommands ?? throw new ArgumentNullException(nameof(craftingCommands));
        _hud = hud ?? throw new ArgumentNullException(nameof(hud));
        _forgeUi = forgeUi ?? throw new ArgumentNullException(nameof(forgeUi));
        _boardingRules = boardingRules ?? throw new ArgumentNullException(nameof(boardingRules));
        _dungeonCombat = dungeonCombat ?? throw new ArgumentNullException(nameof(dungeonCombat));
        _dungeonRewards = dungeonRewards ?? throw new ArgumentNullException(nameof(dungeonRewards));
        _dungeonCommands = dungeonCommands ?? throw new ArgumentNullException(nameof(dungeonCommands));
        _dungeonTactics = dungeonTactics ?? throw new ArgumentNullException(nameof(dungeonTactics));
        _dungeonOperations = dungeonOperations ?? throw new ArgumentNullException(nameof(dungeonOperations));
        _dungeonSettlement = dungeonSettlement ?? throw new ArgumentNullException(nameof(dungeonSettlement));
        _dungeonPanel = dungeonPanel ?? throw new ArgumentNullException(nameof(dungeonPanel));
        _dungeons = dungeons ?? throw new ArgumentNullException(nameof(dungeons));
        _story = story ?? throw new ArgumentNullException(nameof(story));
        _bars = bars ?? throw new ArgumentNullException(nameof(bars));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _dialogue = dialogue ?? throw new ArgumentNullException(nameof(dialogue));
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _recipeRegistration = recipeRegistration ?? throw new ArgumentNullException(nameof(recipeRegistration));
        _inventories = inventories ?? throw new ArgumentNullException(nameof(inventories));
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI service access requires the Unity main thread.");
    }
}
