# EquipmentTargeting

**What you can build with this: your own equipment-targeting decision, without a single Harmony
patch.** Your mod answers "what may the autopilot use?"; the API owns the only hook into
the game, and the game keeps every native protection.

A sample/test BepInEx mod for the **VG Mod API** (development build; requires the matching
unreleased API, not public 0.2.8).

## What it demonstrates (the abilities)

| Ability | How the example uses it |
|---|---|
| **Equipment policy** (`IEquipmentService.ConfigurePlayerTractorModules`) | When `ExtraAutoBeam` is on, the autopilot may borrow one free manual beam; when off the callback returns `null` and vanilla behavior is untouched. |
| **Abstain by returning null** | A registration that abstains is invisible — no zero-limits, no half-applied state. The first non-null answer wins; yours is not silently overridden by later registrations. |
| **Mod-owned policy, native safety** | The `+1` capacity is this mod's formula. Target eligibility, crew, cargo and occupied-beam protections stay the game's — the API never exposes them and you never reimplement them. |
| **Typed global preference** | The knob is a plain `ConfigEntry<bool>` — BepInEx config owns the file; no API store, no reflection. |
| **Service acquisition + disposal** | `ModApi.Services` in `Start()` behind an `InvalidOperationException` guard; the registration is disposed in `OnDestroy`, after which the game reverts to vanilla policy. |

The tooltip and multi-setting showcases live where they belong: **UiSurfaces** (typed settings,
tooltip annotations on module families and mastery badges) and **StationCommerce** (a maker's tip
on the example's own trade good).

## Try it

1. Deploy `EquipmentTargeting.dll` next to the API and set `ExtraAutoBeam = true` in
   `BepInEx/config/vgmodapi.example.equipment-targeting.cfg`.
2. Dock somewhere with loot. While every automatic beam is busy, the autopilot now also uses one
   free manual beam. With the setting off, it never does.
3. Disable the mod: the extra beam is gone — the game's own beam rules were never replaced.

The `equipment-targeting` E2E case (`make e2e E2E_CASE=equipment-targeting`) verifies exactly this sequence
deterministically in a real session, including the off ⇒ vanilla-cap negative and full fixture
restore.
