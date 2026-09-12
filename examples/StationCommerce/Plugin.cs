using System;
using System.Linq;
using BepInEx;
using VGModAPI;

namespace StationCommerce;

/// <summary>
/// Sample/test mod demonstrating the content a station is made of: owned **trade goods**, the
/// **recipe** that manufactures them, and a **bar contact** who stands in a real station's bar with
/// a mission attached.
///
/// It is built twice, as two separately loaded assemblies with different BepInEx IDs (`AuthorA` and
/// `AuthorB`), to show authenticated provider composition: both register the SAME author-local IDs
/// ("silo-container", "contact", …) and neither can touch the other's rows, because the API scopes
/// every declaration to the provider that registered it.
///
/// The API persists supported presentation and declaration state. This plugin has no save hook and
/// no serializer.
/// </summary>
[BepInPlugin(Id, "Station Commerce example (" + Variant + ")", "1.0.0")]
[BepInDependency(ModApi.PluginId, "0.2.10")]
public sealed class Plugin : BaseUnityPlugin
{
#if AUTHOR_B
    public const string Id = "vgmodapi.example.station-commerce-b";
    private const string Variant = "B";
    private const string PortraitName = "MercMan";
    private const bool PatronIsMale = true;
#else
    public const string Id = "vgmodapi.example.station-commerce-a";
    private const string Variant = "A";
    private const string PortraitName = "MercWoman";
    private const bool PatronIsMale = false;
#endif

    // Author-local identities. Both variants deliberately use the SAME strings.
    private const string ItemDef = "silo-container";
    private const string RecipeDef = "container-recipe";
    private const string PatronDef = "contact";
    private const string LinkedStoryDef = "contact-errand";

    private IOwnedItemProvider? _items;
    private IOwnedRecipeProvider? _recipes;
    private IBarProvider? _bars;
    private IStoryProvider? _story;
    private IGameService? _games;
    private IHudRegistration? _hud;

    private IStoryDefinition? _errand;
    private string _itemStatus = "not registered";
    private string _recipeStatus = "not registered";
    private string _patronStatus = "not placed";
    private string _stationStatus = "default ownership";
    private int _interactions;

    private void Awake()
    {
        // Registration is deferred to Start(): BepInEx only populates PluginInfos[].Instance after
        // Awake returns, so the item/recipe/bar/story providers' host authentication cannot succeed
        // here. Acquiring in Awake returns null and silently registers nothing.
    }

    private void Start()
    {
        // Deliberately register the RECIPE first: owned item dependencies resolve automatically later,
        // so an author never has to order their declarations by hand.
        _recipes = ModApi.Services.RecipeRegistration.AcquireProvider(this);
        if (_recipes == null) Logger.LogWarning("Recipe provider unavailable.");
        else
        {
            var status = _recipes.Register(new OwnedRecipeDefinition(RecipeDef, 1, "Silo Container", 100, 30,
                new[] { new OwnedRecipeIngredient(RecipeItemReference.Vanilla("Carbon"), 2) },
                new OwnedRecipeIngredient(RecipeItemReference.FromOwned(new OwnedItemReference(Id, ItemDef)), 1)));
            _recipeStatus = status.ToString();
        }

        _items = ModApi.Services.Items.AcquireProvider(this);
        if (_items == null) Logger.LogWarning("Item provider unavailable.");
        else
        {
            var status = _items.Register(new OwnedItemDefinition(ItemDef, 1, "Silo Container",
                "A manufactured container. Plain trade goods, not an installable silo.", "Carbon", 15, 100,
                OwnedItemStorage.Armory));
            _itemStatus = status.ToString();
        }

        // A contact is more interesting with something to say: register a small errand and link it to
        // the patron by content id. Composition across two owned providers of the same plugin.
        var story = ModApi.Services.Story.AcquireProvider(this);
        _story = story.Provider;
        if (_story == null) Logger.LogWarning("Story provider unavailable: " + story.Diagnostic);
        else
            _errand = _story.Register(new StoryMissionDefinition(LinkedStoryDef, "A container short",
                "The contact is one container short of a shipment. Hear them out.",
                new StoryFactionId("Fanatics"),
                new[] { new StoryStep("Speak to the contact", new[] { StoryObjective.Scripted("talk", "Speak twice", 2) }) },
                new[] { StoryReward.Credits(1) })).Definition;

        _bars = ModApi.Services.Bars.AcquireProvider(this).Provider;
        if (_bars == null) Logger.LogWarning("Bar provider unavailable (check the API's [Bars] Enabled setting).");

        _games = ModApi.Services.Game;
        _hud = ModApi.Services.Hud.Register(Id, "panel", OnHud);
        RefreshPanel();
    }

    /// <summary>The first visited station the game can report — a real id, never an invented GUID.</summary>
    private NavigationStation? FirstVisitedStation()
    {
        var game = _games?.Current;
        if (game == null) return null;
        var stations = game.Navigation.GetStations(visitedOnly: true);
        return stations.Status != NavigationStatus.Succeeded ? null : stations.Stations.FirstOrDefault();
    }

    private void PlaceContact()
    {
        if (_bars == null) { _patronStatus = "bar provider unavailable"; return; }
        var station = FirstVisitedStation();
        if (station == null) { _patronStatus = "no visited station yet"; return; }

        // The declared contact is placed by the API once the game and its save data are ready; there
        // is no per-session placement call to make.
        var result = _bars.Register(
            new BarPatronDefinition(PatronDef, station.Id, "Contact " + Variant,
                "An independently authored contact who deals in containers.",
                new BarPatronPresentation("station-commerce-" + Variant, CharacterPortrait.Named(PortraitName), isMale: PatronIsMale),
                BarPatronRetention.Persistent,
                _errand == null ? null : new StoryContentId(_story!.ProviderId, LinkedStoryDef)),
            _ => { _interactions++; RefreshPanel(); });
        _patronStatus = result.Status + " @ " + (station.Name ?? station.Id);
    }

    /// <summary>
    /// Additive keeps the vanilla roster and adds this contact; Exclusive asks to own the station's
    /// roster outright and must be permitted explicitly in the API configuration.
    /// </summary>
    private void ConfigureStation(BarRosterOwnership ownership)
    {
        if (_bars == null) { _stationStatus = "bar provider unavailable"; return; }
        var station = FirstVisitedStation();
        if (station == null) { _stationStatus = "no visited station yet"; return; }
        _stationStatus = ownership + ": " + _bars.ConfigureStation(station.Id, ownership).Status;
    }

    private void RefreshPanel()
    {
        if (_hud == null) return;
        var station = FirstVisitedStation();
        _hud.Update(null, new HudPanel("Station Commerce " + Variant,
            new[]
            {
                new HudRow("goods", "Trade goods: " + _itemStatus, "owned item declaration",
                    "Registered in Start(). The recipe was declared BEFORE the item it produces; owned "
                    + "dependencies resolve automatically, so declaration order is not the author's problem."),
                new HudRow("recipe", "Recipe: " + _recipeStatus, "owned recipe declaration",
                    "Find(\"" + RecipeDef + "\") supplies a RecipeId to the catalog, quote and crafting-command APIs."),
                new HudRow("place", "Place contact",
                    "declare the bar contact at the first visited station",
                    "Uses a real station id discovered through navigation. The API places the declared contact "
                    + "once the game and its save data are ready — there is no per-session placement call.",
                    clickable: _bars != null && station != null),
                new HudRow("additive", "Station ownership: additive",
                    "keep the vanilla roster and add this contact",
                    "The ordinary, polite choice; other mods' contacts remain.",
                    clickable: _bars != null && station != null),
                new HudRow("exclusive", "Station ownership: exclusive",
                    "request sole ownership of this station's roster",
                    "Refused unless exclusive permission is granted explicitly in the API configuration.",
                    clickable: _bars != null && station != null),
                new HudRow("station", "Station: " + (station?.Name ?? station?.Id ?? "none visited") + " | " + _stationStatus,
                    "the station these declarations target"),
                new HudRow("status", "Contact: " + _patronStatus + " | interactions: " + _interactions,
                    "The interaction counter is process-local example behavior, not persisted narrative state."),
            },
            closable: false));
    }

    private void OnHud(HudInteraction interaction)
    {
        if (interaction.Kind != HudInteractionKind.Row) return;
        try
        {
            switch (interaction.RowId)
            {
                case "place": PlaceContact(); break;
                case "additive": ConfigureStation(BarRosterOwnership.Additive); break;
                case "exclusive": ConfigureStation(BarRosterOwnership.Exclusive); break;
            }
            RefreshPanel();
        }
        catch (Exception error) { Logger.LogError(error); RefreshPanel(); }
    }

    private void OnDestroy()
    {
        var hud = _hud; _hud = null; hud?.Dispose();
        // Releasing a provider removes runtime behavior but preserves persistent rows for a later
        // registration; per-save removal is a separate, reversible action on the live patron object.
        _bars?.Dispose(); _bars = null;
        _story?.Dispose(); _story = null;
        _items?.Dispose(); _items = null;
        _recipes?.Dispose(); _recipes = null;
        _errand = null; _games = null;
    }
}
