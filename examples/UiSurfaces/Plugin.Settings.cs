using BepInEx.Configuration;
using VGModAPI;

namespace UiSurfaces;

public sealed partial class Plugin
{
    private ConfigEntry<bool> _countOpens = null!;
    private ConfigEntry<int> _goal = null!;
    private ConfigEntry<float> _opacity = null!;
    private ConfigEntry<string> _theme = null!;
    private ConfigEntry<bool> _showGoal = null!;
    private ConfigEntry<bool> _annotate = null!;
    private IModSettingsProvider? _settings;

    private void ConfigurePreferences()
    {
        // Global preferences belong to BepInEx config, NOT to WindowVisits' per-save payload.
        _countOpens = Config.Bind("Window", "CountOpens", true, "Record window opens in the current save.");
        _goal = Config.Bind("Window", "VisitGoal", 10, new ConfigDescription("Window-open goal displayed for each save.", new AcceptableValueRange<int>(1, 100)));
        _opacity = Config.Bind("Window", "Opacity", .9f, new ConfigDescription("Example window opacity.", new AcceptableValueRange<float>(.3f, 1f)));
        _theme = Config.Bind("Window", "Theme", "blue", new ConfigDescription("Example window theme.", new AcceptableValueList<string>("blue", "amber")));
        _showGoal = Config.Bind("Behavior", "ShowVisitGoal", true, "Show the global visit goal beside this save's count.");
        _annotate = Config.Bind("Tooltips", "AnnotateModules", false, "Add this mod's note to ship module stat lists and the Engineering mastery badge.");
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
        Publish(new BoolModSetting("show-goal", "Behavior", "Show visit goal", "Show or hide the global goal beside this save's progress.",
            (bool)_showGoal.DefaultValue, () => _showGoal.Value, value => _showGoal.Value = value, order: 0));
        Publish(new BoolModSetting("annotate-tooltips", "Tooltips", "Annotate modules and mastery", "Opt in to the tooltip contributions demonstrated below.",
            (bool)_annotate.DefaultValue, () => _annotate.Value, value => _annotate.Value = value, order: 0));
        // Tooltip contributions belong to the UI surfaces this example owns. They are registered
        // unconditionally and gated by a typed preference: the callback abstains when it is off.
        _moduleTip = ModApi.Services.Tooltips.RegisterShipModule(Id, (module, tip) =>
        {
            if (!_annotate.Value) return;
            tip.AddLine($"UiSurfaces: {module.Kind} module, quality {module.QualityLevel}"
                + (module.Tractor is { } tractor ? $" with {tractor.ManualBeamCount} manual beam(s)" : ""));
        });
        _treeTip = ModApi.Services.Tooltips.RegisterSkillTree(Id, (tree, tip) =>
        {
            if (!_annotate.Value || tree.Specialization != CommanderSpecialization.Engineering) return;
            tip.AddLine($"UiSurfaces: Engineering mastery {tree.MasteryLevel}/{tree.MaximumLevel}", TooltipTextStyle.Bonus);
        });
    }

    private void Publish(ModSettingDefinition definition)
    {
        var result = _settings!.Register(definition);
        if (result != ModSettingRegistrationStatus.Registered)
            Logger.LogWarning($"Example setting '{definition.LocalId}' refused: {result}");
    }
    private void PreferenceChanged(object sender, SettingChangedEventArgs args) => RefreshWindow();
}
