using BepInEx.Configuration;
using VGModAPI;

namespace UiSurfaces;

public sealed partial class Plugin
{
    private ConfigEntry<bool> _countOpens = null!;
    private ConfigEntry<int> _goal = null!;
    private ConfigEntry<float> _opacity = null!;
    private ConfigEntry<string> _theme = null!;
    private IModSettingsProvider? _settings;

    private void ConfigurePreferences()
    {
        // Global preferences belong to BepInEx config, NOT to WindowVisits' per-save payload.
        _countOpens = Config.Bind("Window", "CountOpens", true, "Record window opens in the current save.");
        _goal = Config.Bind("Window", "VisitGoal", 10, new ConfigDescription("Window-open goal displayed for each save.", new AcceptableValueRange<int>(1, 100)));
        _opacity = Config.Bind("Window", "Opacity", .9f, new ConfigDescription("Example window opacity.", new AcceptableValueRange<float>(.3f, 1f)));
        _theme = Config.Bind("Window", "Theme", "blue", new ConfigDescription("Example window theme.", new AcceptableValueList<string>("blue", "amber")));
        Config.SettingChanged += PreferenceChanged;
    }

    private void Start()
    {
        // Instance authentication is available after BepInEx has finished Awake.
        _settings = ModApi.Services.Settings.AcquireProvider(this);
        if (_settings == null) { Logger.LogWarning("Example settings unavailable."); return; }
        Publish(new BoolModSetting("count-opens", "Window", "Count window opens", "Pause counting without erasing this save's progress.",
            (bool)_countOpens.DefaultValue, () => _countOpens.Value, value => _countOpens.Value = value, order: 0));
        Publish(new IntModSetting("goal", "Window", "Visit goal", "Global display target; does not change saved progress.",
            (int)_goal.DefaultValue, 1, 100, 1, () => _goal.Value, value => _goal.Value = value, order: 1));
        Publish(new FloatModSetting("opacity", "Window", "Window opacity", "Changes the open window immediately.",
            (float)_opacity.DefaultValue, .3f, 1f, .1f, () => _opacity.Value, value => _opacity.Value = value, order: 2));
        Publish(new ChoiceModSetting("theme", "Window", "Color theme", "Changes the open window immediately.",
            (string)_theme.DefaultValue, new[] { new ModSettingChoice("blue", "Blue"), new ModSettingChoice("amber", "Amber") },
            () => _theme.Value, value => _theme.Value = value, order: 3));
    }

    private void Publish(ModSettingDefinition definition)
    {
        var result = _settings!.Register(definition);
        if (result != ModSettingRegistrationStatus.Registered)
            Logger.LogWarning($"Example setting '{definition.LocalId}' refused: {result}");
    }
    private void PreferenceChanged(object sender, SettingChangedEventArgs args) => RefreshWindow();
}
