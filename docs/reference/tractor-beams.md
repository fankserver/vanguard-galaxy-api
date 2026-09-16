# Equipment, skill trees, and tooltips

These independent surfaces are unreleased; public API 0.2.8 does not contain them.

## Configure player tractor modules

```csharp
var api = ModApi.Services;
var targeting = api.Equipment.ConfigurePlayerTractorModules("my.mod", module =>
    new TractorTargeting(module.BeamCount + 1, allowManualBorrowing: true));
```

`TractorModule` carries read-only values for this callback, not a native object:
`BeamCount` is vanilla `amountOfBeams`; `ManualBeamCount` is the UI's **Manual Tractor
Beams** (`amountOfBonusBeams`). No mastery or presentation policy lives on the module.

Vanilla scans its requested pool first. Only after it finds no beam can the API
borrow a free one. `AutomaticBeamLimit` is clamped between the base automatic count
and total physical count. Busy beams in either pool count toward the automatic
limit. Manual borrowing is separately opt-in. Occupied beams are never stolen.
Candidate expansion preserves native ordering, ownership and crew/brig eligibility.
Actual tracking and cargo-space checks remain vanilla. Rules apply to the player
ship, not AI ships or carrier fighters.

Return null to leave behavior to the next registration or vanilla; the first
non-null result wins in registration order. Callbacks are synchronous, main-thread,
read-only evaluations; errors abstain and recursive evaluation falls back to vanilla.
Dispose the registration to remove the rule. There is no polling or session handling
for ordinary consumers. Unavailable integration leaves vanilla unchanged.

## Read commander skill trees

```csharp
var tree = api.SkillTrees.Get(CommanderSpecialization.Engineering);
int level = tree?.MasteryLevel ?? 0;
```

Specialization names match the game's enum, including Engineering, Mining, Economy,
Industrial and Leadership. `Identifier` is the native skill-tree identifier (for
example Engineering resolves through vanilla to its tree); `MasteryLevel` and
`MaximumLevel` are current native values. Get returns null without a commander/tree
or when integration is unavailable. Retained values do not follow a replacement game.
Mastery scaling formulas remain in mods, not in the targeting API.

## Extend tooltips independently

```csharp
var moduleTip = api.Tooltips.RegisterTractorModule("my.mod", (module, tooltip) =>
{
    if (module.ManualBeamCount > 0)
        tooltip.AddLine("Manual beams can also target automatically");
});
var masteryTip = api.Tooltips.RegisterSkillTree("my.mod", (tree, tooltip) =>
{
    if (tree.Specialization == CommanderSpecialization.Engineering)
        tooltip.AddLine($"Mastery: {tree.MasteryLevel}", TooltipTextStyle.Bonus)
            .Append(" (my mod)", TooltipTextStyle.Details);
});
```

No equipment rule is required to add tooltip text. All skill-tree mastery tooltips
are supported, not only Engineering. Lines append after native content; Bonus and
Details use the game palette. Module stats use plain text with an empty numeric value.
Native module-stat caching is preserved: a config change does not rebuild existing
stats. Use ASCII strings for the game's pixel font.

Tooltip builders are valid only during their callback. Failing contributions are
discarded without suppressing other mods. Disposal removes a registration; API
shutdown disables all surfaces. None of these APIs expose game or Unity types.
