# Station Commerce

**What you can build with this: your own trade goods and the people who deal in them.** A
manufactured item, the recipe that produces it, and a bar contact standing in a real station's bar
with a mission attached — all persisted by the API, with no save hook of your own.

A sample/test BepInEx mod for the **VG Mod API** (v0.2.10+), built **twice** as two separately loaded
assemblies to demonstrate authenticated provider composition.

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Owned items** (`IOwnedItemService`) | A "Silo Container" trade good with a base material, mass and stack size. |
| **Owned recipes** (`IOwnedRecipeService`) | The recipe that manufactures it, declared **before** the item it produces — owned dependencies resolve automatically, so declaration order is not the author's problem. |
| **Cross-provider references** | `RecipeItemReference.FromOwned` points at this plugin's own item; `RecipeItemReference.Vanilla` points at a base-game material. |
| **Bar contacts** (`IBarProvider.Register`) | A persistent patron with a seeded presentation and a named portrait. The API places the declared contact once the game and its save data are ready — there is **no per-session placement call**. |
| **Real station discovery** | The station id comes from `game.Navigation.GetStations(visitedOnly: true)`, never an invented GUID. |
| **Story-linked contacts** (`StoryContentId`) | A small errand is registered through the story provider and linked to the patron, composing two owned providers inside one plugin. |
| **Roster ownership** (`ConfigureStation`) | `Additive` keeps the vanilla roster; `Exclusive` requests sole ownership and is refused unless permitted explicitly in the API configuration. |
| **Interaction callbacks** | The patron's interact callback increments a counter — deliberately process-local example behavior, *not* persisted narrative state. |
| **Independent providers, same local IDs** | Variants **A** and **B** register identical author-local IDs (`silo-container`, `contact`, …). Neither can touch the other's rows: the API scopes every declaration to the provider that registered it. |
| **Inventory movement** (`InventoryMoves.cs`) | Game-bound inventories and transfer completion, rather than a consumer recovery or session-token protocol. |

## The two variants

| Project | Assembly | BepInEx ID |
|---|---|---|
| `StationCommerce.csproj` | `StationCommerce.dll` | `vgmodapi.example.station-commerce-a` |
| `AuthorB/StationCommerceB.csproj` | `StationCommerceB.dll` | `vgmodapi.example.station-commerce-b` |

Variant B is the *same* `Plugin.cs` compiled with `AUTHOR_B`. Load both at once to see two independent
authors coexist on one station with colliding local IDs — that is the whole point of the pair.

## The HUD buttons

| Button | What it does |
|---|---|
| **Place contact** | Declares the bar contact at the first visited station. |
| **Station ownership: additive** | Keeps the vanilla roster and adds this contact. |
| **Station ownership: exclusive** | Requests sole ownership; expect a refusal unless it was permitted in the API configuration. |

The goods and recipe rows are status only — both are registered during `Start()`.

## Lifetime note

Every provider here (items, recipes, bars, story) is **instance-authenticated** and therefore acquired
in `Start()`, not `Awake()`. BepInEx only populates `Chainloader.PluginInfos[].Instance` *after*
`Awake()` returns, so acquiring in `Awake()` returns null and silently registers nothing.

## Persistence

The API persists supported declaration and presentation state. This plugin has no save hooks or
serializers. Releasing a provider removes runtime behavior but preserves persistent rows for a later
registration; explicit per-save removal is a separate, reversible action on the live patron object.

## Build & deploy

```bash
dotnet build examples/StationCommerce/StationCommerce.csproj
dotnet build examples/StationCommerce/AuthorB/StationCommerceB.csproj
```

Deploy the built DLLs into `BepInEx/plugins/`. Enable the API's `[Bars] Enabled` setting for the
contact to appear.

Requires the VG Mod API plugin (≥ **0.2.10**). Use disposable saves when trying example content; do
not install it into an existing campaign without explicit intent and backups. These examples are
never part of the shipped API package.
