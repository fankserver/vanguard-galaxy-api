# Shared HUD — experimental

Requires API 0.1.38. Enable `[Hud] Enabled=true`; `ModApi.Hud` is available only when its inspected bindings succeed. The `hud` capability describes integration availability, not a visible canvas or in-game qualification. `Visible` additionally requires a tracked initialized player and the current active native HUD/cargo-indicator context.

The service provides bounded buttons and information panels, not a window/widget framework. Consumers own their content models, actions, additional preferences and any separate windows. The API owns transient registrations, placement, native presentation and reattachment to the existing HUD canvas. It does not create another general-purpose canvas.

```csharp
var hud = ModApi.Hud;
if (hud == null) return;
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

At most 16 registrations and four simultaneous panels are allowed. Each panel contains at most 32 uniquely identified rows. Limit violations reject the change without discarding prior content. Numeric order followed by ordinal provider/local identity determines placement. Shared lower-right strips scroll horizontally when needed; long panels scroll vertically. Consumers do not choose mutually coordinated offsets. Geometry uses native canvas units and reattaches/rebuilds when the current canvas or viewport changes.

Input checks the registration token, model revision, current session and live surface. Pointer-down revisions are retained through release; keyboard submission uses the current revision. Disposed/replaced entries, hidden surfaces, disabled buttons, non-clickable rows and obsolete models cannot invoke a replacement action. Callbacks are individually isolated and recursive invocation is refused. Content updates need not destroy existing hovered rows when their identities/structure are unchanged.

## Presentation is separate from recipe data

`HudPresentation` identifies a vanilla item, refined material or Forge recipe for rendering. Item/material local IDs correspond to recipe resource IDs; Forge recipe IDs retain their full `forge/...` identity. Unknown providers/identities or conflicting native definitions render without a native asset. Identical repeated native references are coalesced. A blank row label uses the resolved native display name; otherwise the provider label is retained.

The renderer reuses native icons and configures native item tooltips on API-owned rows without cloning vanilla ingredient rows. Material rows have no native item tooltip. A plain provider tooltip is the fallback when no native item tooltip is available; native item tooltips take priority. Labels and fallback tooltips do not interpret rich text. No Unity object, prefab or generated item escapes the contracts.

Rendering is an explicit presentation operation. A Forge recipe icon may invoke native result-preview construction; item tooltip display also uses native presentation behavior. This is deliberately not a catalog/requirements query and must not be used as evidence of created output, inventory, payment or delivery. A presentation failure falls back locally rather than removing other providers' content.

## Verification limits

Host tests cover ownership/coexistence, limits, disposal, revisions, session/surface invalidation, unavailable data and callback failures. Installed-binding checks verify member shapes, not UI execution. Layout, scaling, hover/input, native canvas replacement and coexistence with consumer mods require controlled Unity acceptance. Existing mods using private fixed-coordinate UI are not automatically migrated or arbitrated.
