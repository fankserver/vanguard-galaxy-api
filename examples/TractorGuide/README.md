# TractorGuide

**What you can build with this: gameplay policy and tooltip text for the tractor, without a single
Harmony patch.** A mod decides *what* the autopilot may do and *what readers should know*; the API
owns the only hook into the game.

A sample/test BepInEx mod for the **VG Mod API** (development build; requires the matching
unreleased API, not public 0.2.8).

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Equipment policy** (`IEquipmentService.ConfigurePlayerTractorModules`) | When `ExtraAutoBeam` is on, the autopilot may borrow one free manual beam; when off the callback returns `null` and vanilla behavior is untouched. |
| **Abstain by returning null** | A registration that abstains is invisible — no zero-limits, no half-applied state. The first non-null answer wins; yours is not silently overridden by later registrations. |
| **Mod-owned policy, native safety** | The `+1` capacity is this mod's formula. Target eligibility, crew, cargo and occupied-beam protections stay the game's — the API never exposes them and you never reimplement them. |
| **Ship-module tooltips** (`ITooltipService.RegisterShipModule`) | Every equipment module family reaches one callback with a typed `ShipModule` snapshot (`Kind`, `DisplayName`, `QualityLevel`, vanilla `StatLines`, tractor sub-snapshot); the example filters on `Kind == Tractor`. |
| **Skill-tree tooltips** (`ITooltipService.RegisterSkillTree`) | Adds a styled line to the Engineering badge's tooltip; filter by `Specialization`. |
| **Item tooltips** (`ITooltipService.RegisterItem`) | Adds a tip to one item's tooltip, selected by the `HighlightItem` setting against `ItemInfo.Identifier`; empty setting ⇒ abstain everywhere. |
| **Reading mastery** (`ISkillTreeService.Get`) | The module line embeds the live Engineering mastery level; reads are plain main-thread calls, usable from inside a tooltip callback. |
| **Typed global preferences** | Both knobs are plain `ConfigEntry` fields — BepInEx config and the Mods settings screen own the UI. No API store, no reflection. |
| **Disposal semantics** | All four registrations are `IDisposable` and disposed in `OnDestroy`; after disposal the game reverts to vanilla rendering/policy. |

## Two contracts worth knowing

**Module stat lists are plain text.** The native stat list renders label text only, so
`TooltipTextStyle` is ignored by `RegisterShipModule` contributions. Item and skill-tree
tooltips do apply the style. Contributions never rebuild existing stats — the game's caching
boundary is preserved; a fresh build includes your lines from then on.

**Acquire services in `Start()`, dispose in `OnDestroy()`.** Registration is plugin-id keyed and
main-thread only. If `ModApi.Services` throws `InvalidOperationException`, the API is not up yet —
retry or stand down, don't crash.

## Try it

1. Deploy `TractorGuide.dll` next to the API and start a game.
2. Main menu → **Mods** → Tractor Guide → enable `ExtraAutoBeam`. Dock somewhere with loot and
   watch the autopilot use one extra beam while every automatic beam is busy.
3. Hover a tractor module: the stat list ends with `Autopilot mastery n/max (TractorGuide)`.
   Hover the Engineering mastery badge: it carries the example's detail line.
4. Set `HighlightItem` to any item identifier (see `BepInEx/plugins` logs or your item-mod's dump);
   hovering that item in inventory gains the tractor tip. Clear it and the tip disappears.
5. Disable the mod: every trace of it is gone from tooltips and beam behavior.

The `tractor-guide` E2E case (`make e2e E2E_CASE=tractor-guide`) verifies exactly this sequence
deterministically in a real session.
