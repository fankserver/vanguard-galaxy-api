using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class ModSettingsPresenter
{
    private readonly ModSettingsService _service;
    private IReadOnlyList<PublishedModSetting> _rows = Array.Empty<PublishedModSetting>();
    private int _selected;

    internal ModSettingsPresenter(ModSettingsService service) => _service = service;
    internal IReadOnlyList<PublishedModSetting> Rows => _rows;
    internal int SelectedIndex => _rows.Count == 0 ? -1 : _selected;
    internal PublishedModSetting? Selected => SelectedIndex < 0 ? null : _rows[_selected];
    internal bool HasSettings(string? providerId) => providerId != null && _service.Snapshot(providerId).Count != 0;

    internal bool Open(string providerId)
    {
        _rows = _service.Snapshot(providerId);
        _selected = 0;
        return _rows.Count != 0;
    }

    internal void Close() { _rows = Array.Empty<PublishedModSetting>(); _selected = 0; }
    internal void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _selected = (_selected + delta + _rows.Count) % _rows.Count;
    }

    internal bool Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        _selected = index;
        return true;
    }

    internal string RowName(int index) => _rows[index].Definition.Name;
    internal string RowValue(int index)
    {
        var row = _rows[index];
        var value = _service.TryRead(row, out var current) ? DisplayValue(row.Definition, current!) : "Unavailable";
        return row.Definition.ApplyMode == ModSettingApplyMode.RestartRequired ? value + " (restart)" : value;
    }

    internal bool Change(int direction)
    {
        var row = Selected;
        if (row == null) return false;
        if (!_service.TryRead(row, out var current))
            return row.Definition is ChoiceModSetting invalidChoice && current is string
                ? _service.TryWrite(row, invalidChoice.DefaultValue)
                : false;
        return row.Definition switch
        {
            BoolModSetting => _service.TryWrite(row, !(bool)current!),
            IntModSetting item => _service.TryWrite(row, Clamp((long)(int)current! + (direction < 0 ? -(long)item.Step : item.Step), item.Minimum, item.Maximum)),
            FloatModSetting item => _service.TryWrite(row, Clamp((float)current! + (direction < 0 ? -item.Step : item.Step), item.Minimum, item.Maximum)),
            ChoiceModSetting item => ChangeChoice(row, item, (string)current!, direction),
            _ => false
        };
    }

    internal bool Reset() => Selected != null && _service.TryReset(Selected);

    internal string Details()
    {
        var row = Selected;
        if (row == null) return "No in-game settings published.";
        var definition = row.Definition;
        var value = _service.TryRead(row, out var current) ? DisplayValue(definition, current!) : "Unavailable";
        var restart = definition.ApplyMode == ModSettingApplyMode.RestartRequired ? "\nApplies after restart." : "";
        return definition.Group + " \u2014 " + definition.Name + "\nCurrent: " + value + restart + "\n\n" + definition.Description;
    }

    internal string DecreaseLabel => Selected?.Definition switch
    {
        BoolModSetting => "Toggle",
        ChoiceModSetting => "Previous value",
        _ => "Decrease"
    };
    internal string IncreaseLabel => Selected?.Definition is ChoiceModSetting ? "Next value" : "Increase";
    internal bool ShowIncrease => Selected?.Definition is IntModSetting or FloatModSetting or ChoiceModSetting;

    internal string ValueLabel()
    {
        var row = Selected;
        return row != null && _service.TryRead(row, out var value) ? DisplayValue(row.Definition, value!) : "Unavailable";
    }

    private bool ChangeChoice(PublishedModSetting row, ChoiceModSetting setting, string current, int direction)
    {
        var index = setting.Choices.ToList().FindIndex(choice => choice.Value == current);
        if (index < 0) return false;
        index = (index + (direction < 0 ? -1 : 1) + setting.Choices.Count) % setting.Choices.Count;
        return _service.TryWrite(row, setting.Choices[index].Value);
    }

    private static string DisplayValue(ModSettingDefinition definition, object value) => definition switch
    {
        BoolModSetting => (bool)value ? "On" : "Off",
        IntModSetting => ((int)value).ToString(CultureInfo.InvariantCulture),
        FloatModSetting => ((float)value).ToString("0.###", CultureInfo.InvariantCulture),
        ChoiceModSetting item => item.Choices.First(choice => choice.Value == (string)value).Name,
        _ => "Unavailable"
    };

    private static int Clamp(long value, int minimum, int maximum) => (int)Math.Max(minimum, Math.Min(maximum, value));
    private static float Clamp(float value, float minimum, float maximum) => Math.Max(minimum, Math.Min(maximum, value));
}
