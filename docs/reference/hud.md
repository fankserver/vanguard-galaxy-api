# Shared HUD — experimental

`ModApi.Services.Hud` exposes a stable `IHudService`. It initializes automatically. Its typed `Availability` requires inspected bindings; it does not imply a visible canvas. `AvailabilityChanged` reports health changes independently of surface visibility. `Visible` additionally requires a tracked initialized player and the current active native HUD/cargo-indicator context.

The service provides bounded buttons and information panels, not a window/widget framework. Consumers own their content models, actions, additional preferences and any separate windows. Use the [gameplay UI host](gameplay-ui.md) to parent those windows, and shared HUD buttons as their launchers. The API owns transient registrations, placement, native presentation and reattachment to the existing HUD canvas. It does not create another general-purpose canvas.

```csharp
var hud = ModApi.Services.Hud;
if (!hud.Availability.IsAvailable) return;
var registration = hud.Register(pluginId, "status", interaction =>
{
    if (interaction.Kind == HudInteractionKind.ClosePanel) HideMyPanel();
    if (interaction.Kind == HudInteractionKind.Row) SelectMyRow(interaction.RowId);
});
registration.Update(null, new HudPanel("Production target", new[]
{
    new HudRow("materials", "Requirements", "Unavailable away from the relevant inventory")
}));
// Update only when content changes; dispose when the provider stops.
```

## Identity, lifetime and input

Provider and local identifiers jointly identify a registration. Same-provider duplicates fail; different providers can reuse a local ID. Only the returned registration removes or changes that entry. Namespacing is not a security boundary between same-process mods. Registration and callback disposal are main-thread-only.

Registrations and their latest models survive HUD hide/show, canvas replacement and session replacement until disposed. No save hooks are needed for this transient API-owned UI lifetime. Models remain provider-owned: clear or replace session-specific content on session change. They are not persisted by this API. Updating with null hides the corresponding button or panel.

Registrations and panels have no shared count quota: another installed mod cannot exhaust a HUD allocation and prevent your mod from registering. Screen space is bounded by the scrollable layouts, not by rejecting later providers. Each panel contains at most 32 uniquely identified rows; invalid panel definitions are rejected without replacing prior content. Numeric order followed by ordinal provider/local identity determines placement. Panels size to their content within the available HUD height; very short viewports that cannot fit panel controls suppress the panel strip rather than overlap launcher rows. A registration with both a button and panel renders its action inside the panel footer; its corner does not move the panel. Button-only registrations use the shared corner layouts below. Panels remain in their lower HUD area and scroll horizontally when needed; long panels scroll vertically. Geometry uses native canvas units and reattaches/rebuilds when the current canvas or viewport changes.

`Register` throws `InvalidOperationException` for a duplicate provider/local identity,
not because other mods filled the HUD. Invalid arguments, wrong-thread access and
registration after service disposal remain programming errors. No partial entry is
added on failure. Keep a registration for the mod's lifetime and update its presentation
rather than repeatedly disposing and registering on transient context changes.

A visible button should be disabled, with an explanatory tooltip, while its action is
unavailable. Use `Update(null, null)` when there is no useful entry to display; do not
leave an enabled button that silently does nothing.

Input checks the registration token, model revision, current session and live surface. Pointer-down revisions are retained through release; keyboard submission uses the current revision. Disposed/replaced entries, hidden surfaces, disabled buttons, non-clickable rows and obsolete models cannot invoke a replacement action. Callbacks are individually isolated and recursive invocation is refused. Content updates need not destroy existing hovered rows when their identities/structure are unchanged.

## Launcher corners and visual identity

Corner selection and semantic icons require API **0.2.9** or later. The existing
`HudButton(label, tooltip, enabled)` constructor remains supported and produces a
text-only bottom-right launcher. The explicit overload selects a corner and an
optional icon:

```csharp
var stockpile = hud.Register(pluginId, "stockpile", _ => ToggleStockpile(), order: 0);
stockpile.Update(new HudButton("Stockpile", HudCorner.TopRight, HudIcon.Storage,
    tooltip: "Station stockpile"), null);
var refinery = hud.Register(pluginId, "refinery", _ => ToggleRefinery(), order: 1);
refinery.Update(new HudButton("Refinery", HudCorner.TopRight, HudIcon.Refinery,
    tooltip: "Refinery jobs"), null);
var status = hud.Register(pluginId, "status", _ => ToggleStatus());
status.Update(new HudButton("Status", HudCorner.BottomLeft), null);
```

**Each corner is one API-managed layout across all mods**, not a separate area per
mod. Every standalone button independently selects `TopLeft`, `TopRight`, `BottomLeft`
or `BottomRight`. If mod X adds one top-right icon and mod Y adds top-right and
bottom-right icons, the two top-right icons occupy distinct slots in the same row;
Y's bottom-right icon occupies the bottom-right row. No coordinate agreement is needed.

- Within each corner, sort by registration `order`, then ordinal provider ID, then
  local ID. Registration timing does not determine the order. Ordering starts at the
  selected corner and grows inward; removing a button frees its slot.
- The API owns sizes and gaps. Icons use compact square slots; text-only buttons use
  wider slots. Loading/fallback does not change an icon's slot or shift its neighbors.
- Opposing corners have disjoint, bounded viewports. Crowded rows show a scrollbar
  and scroll horizontally with the scrollbar, wheel or drag instead of wrapping over other rows. All registered buttons remain
  in the row; overflow does not discard later providers.
- Game-specific edge insets leave room for native HUD controls; a corner identifies a
  HUD region, not a promise to touch the literal screen edge. Very small viewports that
  cannot hold a row suppress that row rather than overlap it with another corner.
- Changing a button's corner moves that launcher, not its consumer-owned window.
  Panel actions stay in their footer and always retain their label.

The collision guarantee covers API-managed launchers. Consumer windows and arbitrary
UI placed independently by other mods are not positioned or arbitrated by this service.

`HudIcon.Storage` and `HudIcon.Refinery` identify game concepts, not asset names.
The adapter resolves their vanilla sprites; consumers do not scan assets or provide
atlas coordinates. A label is still required as the fallback and becomes the tooltip
when no custom tooltip is supplied. Unknown enum values are rejected.

While unresolved, the launcher preserves its slot but shows no sprite or label for
up to two seconds. It never enables a spriteless image, preventing a white-box flash.
After that it shows the label in the same slot. Missing or failed resolutions keep
retrying at most once per second per visual while the HUD renders; a late sprite
replaces the fallback without a model update or layout rebuild. Successful results
are shared across launchers and rechecked for destruction. Native surface replacement
clears the cache. Resolution failures do not disable other launchers or the HUD.

For a panel-footer action, an optional resolved icon appears alongside the label;
loading never hides that footer label. Its `Corner` is ignored while its panel exists.

The [GameplayWindow example](https://github.com/fankserver/vanguard-galaxy-api/tree/main/examples/GameplayWindow)
uses a top-right storage launcher and a separately owned window container.

## Presentation is separate from recipe data

`HudPresentation` identifies a vanilla item, refined material or Forge recipe for rendering. Item/material local IDs correspond to recipe resource IDs; Forge recipe IDs retain their full `forge/...` identity. Unknown providers/identities or conflicting native definitions render without a native asset. Identical repeated native references are coalesced. A blank row label uses the resolved native display name; otherwise the provider label is retained.

The renderer reuses native icons and configures native item tooltips on API-owned rows without cloning vanilla ingredient rows. Material rows have no native item tooltip. A plain provider tooltip is the fallback when no native item tooltip is available; native item tooltips take priority. Labels and fallback tooltips do not interpret rich text. No Unity object, prefab or generated item escapes the contracts.

Rendering is an explicit presentation operation. A Forge recipe icon may invoke native result-preview construction; item tooltip display also uses native presentation behavior. This is deliberately not a catalog/requirements query and must not be used as evidence of created output, inventory, payment or delivery. A presentation failure falls back locally rather than removing other providers' content.

## Verification limits

Host tests cover ownership/coexistence, limits, disposal, revisions, session/surface invalidation, unavailable data and callback failures. Installed-binding checks verify member shapes, not UI execution. Layout, scaling, hover/input, native canvas replacement and coexistence with consumer mods require controlled Unity acceptance. Existing mods using private fixed-coordinate UI are not automatically migrated or arbitrated.

## Forge-style recipe views

`HudRow.Ingredient` supplies structured required/available quantities. Availability may be null: the view shows `?`, not zero. Native item rarity colours and tooltips are reused; shortage/availability counts have their own colour. `HudRecipeView` composes these reusable rows into a compact header and optional result section, returned by `ToPanel()` for an ordinary HUD registration. No pinning or inventory mutation is implied. Up to 30 ingredient rows leave room for the optional result heading and row; identities must remain unique across the complete panel.

```csharp
var ingredient = HudRow.Ingredient("plate", "Titanium Plate", required: 143, available: 48,
    presentation: new HudPresentation("vanilla", "TitaniumPlate", HudPresentationKind.Item),
    clickable: true);
var view = new HudRecipeView("Cannon", new[] { ingredient });
registration.Update(new HudButton("Show in Forge"), view.ToPanel());
```

Use resource identifiers from the catalog rather than guessing display-name identifiers. The consumer decides which quantities to display, when to refresh, whether to include a result, and what row/header actions mean. Batch allocation, producer choice and pin lifetime are not shared presentation policy. Successful recipe selection should not be rendered as a technical status message. The ForgeInspector example demonstrates a quote snapshot using this view.
