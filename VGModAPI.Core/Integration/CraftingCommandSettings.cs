using System;
using System.Linq;
using System.Reflection;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

internal sealed partial class RecipeCatalogNativeSource
{
    internal Func<Guid?>? CommandSession { get; set; }
    internal Action<object?>? RefreshCommandUi { get; set; }
    public CraftingSettingsSnapshot ReadSettings(Guid sessionId, RecipeStationHandle? station)
    {
        var player = NativePlayer;
        if (!CommandLive(sessionId, player)) return new(sessionId, false, null, null, null, null, "Player session unavailable.");
        var nativeStation = station == null ? null : ResolveStation(station);
        if (station != null && nativeStation == null) return new(sessionId, false, null, null, null, null, "Station handle is stale.");
        var refinery = nativeStation == null ? null : Get(nativeStation, "refinery");
        var result = new CraftingSettingsSnapshot(sessionId, true, refinery == null ? null : Convert.ToBoolean(Get(refinery, "autoRefine")),
            Convert.ToBoolean(Get(player!, "forgeDepositInCargo")), Convert.ToBoolean(GetStatic("Source.Mining.Refinery", "autoSell")),
            Convert.ToBoolean(CommandCallStatic("Source.Player.Register", "HasFlag", "AutoSell", false)),
            "Auto-refine belongs to the station; cargo delivery and the saved AutoSell flag belong to the player save. Effective auto-sell is the active-session cache.");
        return CommandLive(sessionId, player) ? result : new(sessionId, false, null, null, null, null, "Session changed while reading settings.");
    }
    private CraftingCommandResult ConfigureNative(CraftingCommandRequest request, object player)
    {
        var station = request.Station == null ? null : ResolveStation(request.Station);
        if (request.Setting == CraftingSetting.StationAutoRefine && station == null)
            return CommandResult(request, CraftingCommandStatus.StaleHandle, "Station unavailable.");
        var refinery = station == null ? null : Get(station, "refinery");
        if (request.Setting == CraftingSetting.StationAutoRefine && refinery == null)
            return CommandResult(request, CraftingCommandStatus.Unsupported, "Station has no refinery.");
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.StaleHandle, "Player changed before settings mutation.");
        switch (request.Setting)
        {
            case CraftingSetting.StationAutoRefine: CommandSet(refinery!, "autoRefine", request.SettingValue); break;
            case CraftingSetting.PlayerCargoDelivery: CommandSet(player, "forgeDepositInCargo", request.SettingValue); break;
            case CraftingSetting.PlayerAutoSell:
                CommandCallStatic("Source.Player.Register", "SetFlag", "AutoSell", request.SettingValue);
                _assembly.GetType("Source.Mining.Refinery", true)!.GetField("autoSell", Flags)!.SetValue(null, request.SettingValue);
                break;
            default: return CommandResult(request, CraftingCommandStatus.InvalidRequest, "Unknown setting.");
        }
        if (!CommandLive(request.SessionId, player)) return CommandResult(request, CraftingCommandStatus.Uncertain, "Session changed during settings mutation.", true);
        RefreshCommandUi?.Invoke(station);
        var observed = ReadSettings(request.SessionId, request.Station);
        var matches = observed.Available && (request.Setting switch
        {
            CraftingSetting.StationAutoRefine => observed.StationAutoRefine == request.SettingValue,
            CraftingSetting.PlayerCargoDelivery => observed.PlayerCargoDelivery == request.SettingValue,
            CraftingSetting.PlayerAutoSell => observed.EffectiveAutoSell == request.SettingValue && observed.StoredAutoSellPreference == request.SettingValue,
            _ => false
        });
        return CommandResult(request, matches ? CraftingCommandStatus.Succeeded : CraftingCommandStatus.Uncertain,
            matches ? "Requested setting and native cache verified; no other setting was changed." : "Setting outcome could not be verified.", true);
    }
    private bool CommandLive(Guid sessionId, object? player) => player != null && CommandSession?.Invoke() == sessionId && ReferenceEquals(player, NativePlayer);
    private object? CommandCallStatic(string type, string name, params object[] args) => _assembly.GetType(type, true)!.GetMethods(Flags)
        .Single(method => method.IsStatic && method.Name == name && method.GetParameters().Length == args.Length).Invoke(null, args);
    private static void CommandSet(object instance, string member, object value)
    {
        var target = RequireMember(instance.GetType(), member);
        if (target is FieldInfo field) { if (field.IsInitOnly) throw new InvalidOperationException("Readonly setting field."); field.SetValue(instance, value); }
        else ((PropertyInfo)target).SetValue(instance, value);
    }
    private static CraftingCommandResult CommandResult(CraftingCommandRequest request, CraftingCommandStatus status, string detail, bool invoked = false) =>
        CraftingCommandService.Result(request, status, detail, invoked);
}
