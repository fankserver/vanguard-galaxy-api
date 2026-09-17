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

    internal bool Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return false;
        _selected = index;
        return true;
    }

    internal string RowName(int index)
    {
        var definition = _rows[index].Definition;
        return definition.ApplyMode == ModSettingApplyMode.RestartRequired ? definition.Name + " *" : definition.Name;
    }

    internal string RowValue(int index)
    {
        var row = _rows[index];
        return _service.TryRead(row, out var current) ? DisplayValue(row.Definition, current!) : "Unavailable";
    }

    internal bool TryBool(int index, out bool value)
    {
        value = false;
        if (_rows[index].Definition is not BoolModSetting || !_service.TryRead(_rows[index], out var current)) return false;
        value = (bool)current!;
        return true;
    }

    internal bool SetBool(int index, bool value) =>
        _rows[index].Definition is BoolModSetting && _service.TryWrite(_rows[index], value);

    internal bool TryNumber(int index, out float value, out float minimum, out float maximum, out float step, out bool wholeNumbers)
    {
        value = minimum = maximum = step = 0; wholeNumbers = false;
        var row = _rows[index];
        if (!_service.TryRead(row, out var current)) return false;
        switch (row.Definition)
        {
            case IntModSetting item:
                value = (int)current!; minimum = item.Minimum; maximum = item.Maximum; step = item.Step; wholeNumbers = true;
                return true;
            case FloatModSetting item:
                value = (float)current!; minimum = item.Minimum; maximum = item.Maximum; step = item.Step;
                return true;
            default: return false;
        }
    }

    internal bool SetNumber(int index, float raw)
    {
        var row = _rows[index];
        return row.Definition switch
        {
            IntModSetting item => _service.TryWrite(row, (int)Snap(raw, item.Minimum, item.Maximum, item.Step)),
            FloatModSetting item => _service.TryWrite(row, Snap(raw, item.Minimum, item.Maximum, item.Step)),
            _ => false
        };
    }

    internal bool CycleChoice(int index, int direction)
    {
        var row = _rows[index];
        if (row.Definition is not ChoiceModSetting setting) return false;
        if (!_service.TryRead(row, out var current)) return _service.TryWrite(row, setting.DefaultValue);
        var position = setting.Choices.ToList().FindIndex(choice => choice.Value == (string)current!);
        if (position < 0) return _service.TryWrite(row, setting.DefaultValue);
        position = (position + (direction < 0 ? -1 : 1) + setting.Choices.Count) % setting.Choices.Count;
        return _service.TryWrite(row, setting.Choices[position].Value);
    }

    internal bool ResetAll()
    {
        var changed = false;
        foreach (var row in _rows) changed |= _service.TryReset(row);
        return changed;
    }

    internal string Details()
    {
        var row = Selected;
        if (row == null) return "No in-game settings published.";
        var definition = row.Definition;
        var restart = definition.ApplyMode == ModSettingApplyMode.RestartRequired ? "* Applies after restart." : "";
        if (definition.Description.Length == 0) return restart;
        return restart.Length == 0 ? definition.Description : definition.Description + "\n" + restart;
    }

    private static string DisplayValue(ModSettingDefinition definition, object value) => definition switch
    {
        BoolModSetting => (bool)value ? "On" : "Off",
        IntModSetting => ((int)value).ToString(CultureInfo.InvariantCulture),
        FloatModSetting => ((float)value).ToString("0.###", CultureInfo.InvariantCulture),
        ChoiceModSetting item => item.Choices.FirstOrDefault(choice => choice.Value == (string)value)?.Name ?? "Unavailable",
        _ => "Unavailable"
    };

    private static float Snap(float value, float minimum, float maximum, float step)
    {
        var snapped = step > 0 ? minimum + (float)Math.Round((value - minimum) / step) * step : value;
        return Math.Max(minimum, Math.Min(maximum, snapped));
    }
}
