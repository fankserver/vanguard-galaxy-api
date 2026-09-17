# Mod settings

`ModApi.Services.Settings` lets a loaded mod deliberately publish selected player-facing settings in VGModAPI's **Mods** screen. The mod remains the source of truth: the API reads through the supplied getter and writes through the supplied setter. It does not inspect BepInEx configuration, create another configuration file or persist another copy of the value.

Acquire one authenticated provider from the plugin's `Start()` method. BepInEx assigns `PluginInfo.Instance` only after `Awake()` returns, so acquisition from `Awake()` is refused. Keep and dispose the provider with the plugin lifetime.

```csharp
private IModSettingsProvider? _settings;

private void Start()
{
    _settings = ModApi.Services.Settings.AcquireProvider(this);
    if (_settings == null)
    {
        Logger.LogWarning("VGModAPI in-game settings are unavailable.");
        return;
    }

    var status = _settings.Register(new BoolModSetting(
        "auto-refine",
        "Autopilot",
        "Auto-refine on dock",
        "Turn on Auto-Refine when autopilot docks at a refinery.",
        defaultValue: false,
        getValue: () => CfgAutopilotAutoRefine.Value,
        setValue: value => CfgAutopilotAutoRefine.Value = value));
    Logger.LogInfo("Auto-refine setting registration: " + status);
}

private void OnDestroy()
{
    _settings?.Dispose();
}
```

This is the intended Echo-style use: the existing `ConfigEntry<bool>` still owns persistence and `SettingChanged` behavior. Assigning its `Value` through the menu follows normal BepInEx saving rules.

## Setting types

The initial surface supports the setting shapes used by current mods:

- `BoolModSetting`
- `IntModSetting` with inclusive minimum, maximum and positive step
- `FloatModSetting` with finite inclusive minimum, maximum and positive step
- `ChoiceModSetting` with stable values and separate player-facing labels

Every definition has a provider-local ID, group, player-facing name and description. `(ProviderId, LocalId)` is the stable identity; labels and ordering are presentation only. `ModSettingApplyMode.RestartRequired` labels a stored change honestly when the running mod cannot apply it immediately. Defaults support the menu's **Reset to default** action.

Choice values are exact ordinal identifiers. Labels may change without changing the stored value:

```csharp
_settings.Register(new ChoiceModSetting(
    "stack-deposit-mode", "Autopilot", "Stack deposit mode",
    "Controls how much cargo each autopilot deposit cycle moves.",
    defaultValue: "tiered",
    choices: new[]
    {
        new ModSettingChoice("off", "Off"),
        new ModSettingChoice("tiered", "Tiered"),
        new ModSettingChoice("always", "Always")
    },
    getValue: () => CfgAutopilotStackDepositMode.Value.ToString().ToLowerInvariant(),
    setValue: value => CfgAutopilotStackDepositMode.Value =
        (StackDepositMode)Enum.Parse(typeof(StackDepositMode), value, true)));
```

## Registration and UI behavior

Registration is explicit. Do not publish paths, credentials, migration acknowledgements, diagnostics or settings that are unsafe to change. The API never reflects over a plugin's complete configuration.

`Register` returns `Registered`, `Duplicate`, `InvalidDefinition`, `CapacityExceeded` or `Unavailable`. Registration is main-thread-only, bounded to 128 settings per provider and 1,024 settings process-wide. IDs and text are bounded; numeric ranges, defaults and choice uniqueness are validated before publication.

The Mods screen shows **Settings** only for a selected mod with published settings. Players move through the mod's ordered settings, change or reset the current value, and return to its normal information view. Values are read again whenever rendered and after every write, so external config changes remain authoritative. Getter and setter exceptions are isolated to that setting and reported through the owning plugin identity; an unconfirmed write is not shown as successful.

`IModSettingsService.Menu` reports the shared native Mods-screen health independently of the settings registry. Settings can register even when the inspected native menu is unavailable. Disposing the provider removes all of its published settings. API shutdown invalidates retained providers and settings without invoking consumer callbacks afterward.
