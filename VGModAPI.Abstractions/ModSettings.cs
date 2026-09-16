using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

public enum ModSettingApplyMode { Immediate, RestartRequired }

public enum ModSettingRegistrationStatus
{
    Registered,
    Duplicate,
    InvalidDefinition,
    CapacityExceeded,
    Unavailable
}

/// <summary>A player-facing setting deliberately published by its owning mod.</summary>
public abstract class ModSettingDefinition
{
    protected ModSettingDefinition(string localId, string group, string name, string description,
        ModSettingApplyMode applyMode = ModSettingApplyMode.Immediate, int order = 0)
    {
        LocalId = localId ?? throw new ArgumentNullException(nameof(localId));
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Description = description ?? throw new ArgumentNullException(nameof(description));
        if (!Enum.IsDefined(typeof(ModSettingApplyMode), applyMode)) throw new ArgumentOutOfRangeException(nameof(applyMode));
        ApplyMode = applyMode;
        Order = order;
    }

    public string LocalId { get; }
    public string Group { get; }
    public string Name { get; }
    public string Description { get; }
    public ModSettingApplyMode ApplyMode { get; }
    public int Order { get; }
}

public sealed class BoolModSetting : ModSettingDefinition
{
    public BoolModSetting(string localId, string group, string name, string description, bool defaultValue,
        Func<bool> getValue, Action<bool> setValue, ModSettingApplyMode applyMode = ModSettingApplyMode.Immediate, int order = 0)
        : base(localId, group, name, description, applyMode, order)
    {
        DefaultValue = defaultValue;
        GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
        SetValue = setValue ?? throw new ArgumentNullException(nameof(setValue));
    }
    public bool DefaultValue { get; }
    internal Func<bool> GetValue { get; }
    internal Action<bool> SetValue { get; }
}

public sealed class IntModSetting : ModSettingDefinition
{
    public IntModSetting(string localId, string group, string name, string description, int defaultValue,
        int minimum, int maximum, int step, Func<int> getValue, Action<int> setValue,
        ModSettingApplyMode applyMode = ModSettingApplyMode.Immediate, int order = 0)
        : base(localId, group, name, description, applyMode, order)
    {
        DefaultValue = defaultValue; Minimum = minimum; Maximum = maximum; Step = step;
        GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
        SetValue = setValue ?? throw new ArgumentNullException(nameof(setValue));
    }
    public int DefaultValue { get; }
    public int Minimum { get; }
    public int Maximum { get; }
    public int Step { get; }
    internal Func<int> GetValue { get; }
    internal Action<int> SetValue { get; }
}

public sealed class FloatModSetting : ModSettingDefinition
{
    public FloatModSetting(string localId, string group, string name, string description, float defaultValue,
        float minimum, float maximum, float step, Func<float> getValue, Action<float> setValue,
        ModSettingApplyMode applyMode = ModSettingApplyMode.Immediate, int order = 0)
        : base(localId, group, name, description, applyMode, order)
    {
        DefaultValue = defaultValue; Minimum = minimum; Maximum = maximum; Step = step;
        GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
        SetValue = setValue ?? throw new ArgumentNullException(nameof(setValue));
    }
    public float DefaultValue { get; }
    public float Minimum { get; }
    public float Maximum { get; }
    public float Step { get; }
    internal Func<float> GetValue { get; }
    internal Action<float> SetValue { get; }
}

public sealed class ModSettingChoice
{
    public ModSettingChoice(string value, string name)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
    public string Value { get; }
    public string Name { get; }
}

public sealed class ChoiceModSetting : ModSettingDefinition
{
    public ChoiceModSetting(string localId, string group, string name, string description, string defaultValue,
        IEnumerable<ModSettingChoice> choices, Func<string> getValue, Action<string> setValue,
        ModSettingApplyMode applyMode = ModSettingApplyMode.Immediate, int order = 0)
        : base(localId, group, name, description, applyMode, order)
    {
        DefaultValue = defaultValue ?? throw new ArgumentNullException(nameof(defaultValue));
        if (choices == null) throw new ArgumentNullException(nameof(choices));
        Choices = new ReadOnlyCollection<ModSettingChoice>(choices.Select(choice =>
            choice ?? throw new ArgumentException("A choice cannot be null.", nameof(choices))).ToArray());
        GetValue = getValue ?? throw new ArgumentNullException(nameof(getValue));
        SetValue = setValue ?? throw new ArgumentNullException(nameof(setValue));
    }
    public string DefaultValue { get; }
    public IReadOnlyList<ModSettingChoice> Choices { get; }
    internal Func<string> GetValue { get; }
    internal Action<string> SetValue { get; }
}

public interface IModSettingsService : IServiceStatus
{
    IServiceStatus Menu { get; }
    IModSettingsProvider? AcquireProvider(object pluginInstance);
}

public interface IModSettingsProvider : IDisposable
{
    string ProviderId { get; }
    ModSettingRegistrationStatus Register(ModSettingDefinition setting);
}
