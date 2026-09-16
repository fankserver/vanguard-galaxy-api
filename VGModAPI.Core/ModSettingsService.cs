using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VGModAPI.Core;

internal enum ModSettingKind { Bool, Int, Float, Choice }

internal sealed class PublishedModSetting
{
    internal PublishedModSetting(string providerId, ModSettingDefinition definition)
    { ProviderId = providerId; Definition = definition; }

    internal string ProviderId { get; }
    internal ModSettingDefinition Definition { get; }
    internal ModSettingKind Kind => Definition switch
    {
        BoolModSetting => ModSettingKind.Bool,
        IntModSetting => ModSettingKind.Int,
        FloatModSetting => ModSettingKind.Float,
        ChoiceModSetting => ModSettingKind.Choice,
        _ => throw new InvalidOperationException("Unsupported setting definition.")
    };
}

internal sealed class ModSettingsService : IModSettingsService, IDisposable
{
    internal const int MaximumProviders = 128;
    internal const int MaximumSettingsPerProvider = 128;
    internal const int MaximumSettings = 1024;

    private readonly LifecycleHub _hub;
    private readonly StoryHostAuthenticator _authenticate;
    private readonly IServiceStatus _status;
    private readonly IServiceStatus _menu;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Owner, string Local), PublishedModSetting> _settings = new();
    private bool _disposed;

    internal ModSettingsService(LifecycleHub hub, StoryHostAuthenticator authenticate)
    {
        _hub = hub;
        _authenticate = authenticate ?? throw new ArgumentNullException(nameof(authenticate));
        _status = hub.Services.Get("mod-settings");
        _menu = hub.Services.Get("mod-information-menu");
        hub.SetAvailable("mod-settings", "Player-facing settings registry.");
    }

    public ServiceAvailability Availability => _status.Availability;
    public IServiceStatus Menu { get { _hub.CheckThread(); return _menu; } }
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public IModSettingsProvider? AcquireProvider(object pluginInstance)
        => Acquire(pluginInstance, Assembly.GetCallingAssembly());

    internal IModSettingsProvider? Acquire(object pluginInstance, Assembly callingAssembly)
    {
        _hub.CheckThread();
        if (_disposed || _hub.Services.IsStopping || pluginInstance == null || callingAssembly == null) return null;
        StoryHostPlugin? plugin;
        try { plugin = _authenticate(pluginInstance, callingAssembly); }
        catch { return null; }
        if (plugin == null || !ReferenceEquals(plugin.Assembly, callingAssembly) ||
            _providers.ContainsKey(plugin.PluginId) || _providers.Count >= MaximumProviders) return null;
        var provider = new Provider(this, plugin.PluginId);
        _providers.Add(plugin.PluginId, provider);
        return provider;
    }

    internal IReadOnlyList<PublishedModSetting> Snapshot(string providerId)
    {
        _hub.CheckThread();
        if (_disposed) return Array.Empty<PublishedModSetting>();
        return _settings.Values.Where(setting => setting.ProviderId == providerId)
            .OrderBy(setting => setting.Definition.Group, StringComparer.Ordinal)
            .ThenBy(setting => setting.Definition.Order)
            .ThenBy(setting => setting.Definition.Name, StringComparer.Ordinal)
            .ThenBy(setting => setting.Definition.LocalId, StringComparer.Ordinal)
            .ToArray();
    }

    internal bool TryRead(PublishedModSetting setting, out object? value)
    {
        _hub.CheckThread(); value = null;
        if (!Current(setting)) return false;
        try
        {
            value = setting.Definition switch
            {
                BoolModSetting item => item.GetValue(),
                IntModSetting item => item.GetValue(),
                FloatModSetting item => item.GetValue(),
                ChoiceModSetting item => item.GetValue(),
                _ => null
            };
            return Current(setting) && ValidValue(setting.Definition, value);
        }
        catch (Exception error)
        {
            _hub.ReportSubscriberFailure(setting.ProviderId, error);
            return false;
        }
    }

    internal bool TryWrite(PublishedModSetting setting, object value)
    {
        _hub.CheckThread();
        if (!Current(setting) || !ValidValue(setting.Definition, value)) return false;
        try
        {
            switch (setting.Definition)
            {
                case BoolModSetting item: item.SetValue((bool)value); break;
                case IntModSetting item: item.SetValue((int)value); break;
                case FloatModSetting item: item.SetValue((float)value); break;
                case ChoiceModSetting item: item.SetValue((string)value); break;
                default: return false;
            }
            return Current(setting) && TryRead(setting, out var observed) && EqualsValue(setting.Definition, value, observed);
        }
        catch (Exception error)
        {
            _hub.ReportSubscriberFailure(setting.ProviderId, error);
            return false;
        }
    }

    internal bool TryReset(PublishedModSetting setting) => TryWrite(setting, setting.Definition switch
    {
        BoolModSetting item => item.DefaultValue,
        IntModSetting item => item.DefaultValue,
        FloatModSetting item => item.DefaultValue,
        ChoiceModSetting item => item.DefaultValue,
        _ => throw new InvalidOperationException("Unsupported setting definition.")
    });

    private bool Current(PublishedModSetting setting) => !_disposed &&
        _settings.TryGetValue((setting.ProviderId, setting.Definition.LocalId), out var current) && ReferenceEquals(setting, current);

    private static bool EqualsValue(ModSettingDefinition definition, object expected, object? observed)
    {
        if (definition is not FloatModSetting item || expected is not float left || observed is not float right)
            return Equals(expected, observed);
        var tolerance = item.Step * 0.001f;
        return left.Equals(right) || tolerance > 0 && Math.Abs(left - right) <= tolerance;
    }

    private static bool ValidValue(ModSettingDefinition definition, object? value) => definition switch
    {
        BoolModSetting => value is bool,
        IntModSetting item => value is int integer && integer >= item.Minimum && integer <= item.Maximum,
        FloatModSetting item => value is float number && !float.IsNaN(number) && !float.IsInfinity(number) && number >= item.Minimum && number <= item.Maximum,
        ChoiceModSetting item => value is string choice && item.Choices.Any(option => option.Value == choice),
        _ => false
    };

    private static bool ValidDefinition(ModSettingDefinition definition)
    {
        if (definition == null || !Token(definition.LocalId, 64) || !Text(definition.Group, 80) ||
            !Text(definition.Name, 120) || definition.Description == null || definition.Description.Length > 2048) return false;
        switch (definition)
        {
            case BoolModSetting:
                return true;
            case IntModSetting item:
                return item.Minimum <= item.Maximum && item.Step > 0 && item.DefaultValue >= item.Minimum && item.DefaultValue <= item.Maximum;
            case FloatModSetting item:
                return Finite(item.Minimum) && Finite(item.Maximum) && Finite(item.Step) && Finite(item.DefaultValue) &&
                    item.Minimum <= item.Maximum && item.Step > 0 && item.DefaultValue >= item.Minimum && item.DefaultValue <= item.Maximum;
            case ChoiceModSetting item:
                return item.Choices.Count is > 0 and <= 32 && item.Choices.All(choice => choice != null && Token(choice.Value, 64) && Text(choice.Name, 120)) &&
                    item.Choices.Select(choice => choice.Value).Distinct(StringComparer.Ordinal).Count() == item.Choices.Count &&
                    item.Choices.Any(choice => choice.Value == item.DefaultValue);
            default:
                return false;
        }
    }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private static bool Text(string value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool Token(string value, int maximum) => value.Length is > 0 && value.Length <= maximum &&
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-');

    private sealed class Provider : IModSettingsProvider
    {
        private readonly ModSettingsService _service;
        internal Provider(ModSettingsService service, string providerId) { _service = service; ProviderId = providerId; }
        public string ProviderId { get; }

        public ModSettingRegistrationStatus Register(ModSettingDefinition setting)
        {
            _service._hub.CheckThread();
            if (_service._disposed || !_service._providers.TryGetValue(ProviderId, out var current) || !ReferenceEquals(this, current))
                return ModSettingRegistrationStatus.Unavailable;
            if (!ValidDefinition(setting)) return ModSettingRegistrationStatus.InvalidDefinition;
            var key = (ProviderId, setting.LocalId);
            if (_service._settings.ContainsKey(key)) return ModSettingRegistrationStatus.Duplicate;
            if (_service._settings.Count >= MaximumSettings || _service._settings.Keys.Count(item => item.Owner == ProviderId) >= MaximumSettingsPerProvider)
                return ModSettingRegistrationStatus.CapacityExceeded;
            _service._settings.Add(key, new PublishedModSetting(ProviderId, setting));
            return ModSettingRegistrationStatus.Registered;
        }

        public void Dispose()
        {
            _service._hub.CheckThread();
            if (!_service._providers.TryGetValue(ProviderId, out var current) || !ReferenceEquals(this, current)) return;
            _service._providers.Remove(ProviderId);
            foreach (var key in _service._settings.Keys.Where(key => key.Owner == ProviderId).ToArray()) _service._settings.Remove(key);
        }
    }

    public void Dispose()
    {
        _hub.CheckThread();
        if (_disposed) return;
        _disposed = true;
        _providers.Clear();
        _settings.Clear();
        _hub.SetUnavailable("mod-settings", ServiceUnavailableReason.ApiStopped, "API stopped.");
    }
}
