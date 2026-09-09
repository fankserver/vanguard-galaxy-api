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
    private readonly IBoardingCombatService _boardingCombat;
    private readonly IDungeonRewardService _dungeonRewards;
    private readonly IBoardingCommandService _boardingCommands;
    private readonly IBoardingTacticalService _boardingTactics;
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

    public IBoardingCombatService BoardingCombat { get { CheckThread(); return _boardingCombat; } }
    public IDungeonRewardService DungeonRewards { get { CheckThread(); return _dungeonRewards; } }

    public IBoardingCommandService BoardingCommands { get { CheckThread(); return _boardingCommands; } }

    public IBoardingTacticalService BoardingTactics { get { CheckThread(); return _boardingTactics; } }

    internal ModServices(ILifecycleService lifecycle, IModInformationService mods, ISaveDataService saveData,
        IMissionService missions, ITravelService travel, IStationService station, IRecipeService recipes, IRecipeQuoteService recipeQuotes,
        ICraftingJobService craftingJobs, ICraftingCommandService craftingCommands, IHudService hud, IForgeUiService forgeUi, IBoardingRuleService boardingRules, IBoardingCombatService boardingCombat, IDungeonRewardService dungeonRewards, IBoardingCommandService boardingCommands, IBoardingTacticalService boardingTactics)
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
        _boardingCombat = boardingCombat ?? throw new ArgumentNullException(nameof(boardingCombat));
        _dungeonRewards = dungeonRewards ?? throw new ArgumentNullException(nameof(dungeonRewards));
        _boardingCommands = boardingCommands ?? throw new ArgumentNullException(nameof(boardingCommands));
        _boardingTactics = boardingTactics ?? throw new ArgumentNullException(nameof(boardingTactics));
    }

    internal void CheckThread()
    {
        if (Thread.CurrentThread.ManagedThreadId != _thread)
            throw new InvalidOperationException("VGModAPI service access requires the Unity main thread.");
    }
}
