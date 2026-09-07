using System;

namespace VGModAPI.Core;

internal sealed class ModUpdatePresenter
{
    private readonly ModUpdateService _service;
    private readonly Action<bool> _setAutomatic;
    private string? _confirmation;
    internal bool Confirming => _confirmation != null;
    internal bool ConfirmingAutomatic => _confirmation == "automatic";
    internal bool Automatic => _service.Automatic;
    internal ModUpdatePresenter(ModUpdateService service, Action<bool> setAutomatic) { _service = service; _setAutomatic = setAutomatic; }
    internal void Cancel() => _confirmation = null;
    internal void Sync(System.Collections.Generic.IReadOnlyList<ModInformation> mods) => _service.Sync(mods);
    internal bool Check(ModInformation mod)
    {
        var key = "manual:" + ModUpdateCache.Key(mod);
        if (!_service.Enabled || !ModUpdateHosts.Allowed(mod.Metadata?.UpdateUrl ?? "")) { Cancel(); return false; }
        if (_confirmation != key) { _confirmation = key; return false; }
        Cancel(); return _service.Request(mod.PluginId);
    }
    internal void ToggleAutomatic()
    {
        if (!_service.Enabled) return;
        if (_service.Automatic) { _setAutomatic(false); _service.Automatic = false; Cancel(); return; }
        if (_confirmation != "automatic") { _confirmation = "automatic"; return; }
        _setAutomatic(true); _service.Automatic = true; Cancel();
    }
    internal string Text(ModInformation mod, DateTimeOffset now)
    {
        if (Confirming)
            return "NETWORK CONFIRMATION\n" + (_confirmation == "automatic" ? "Enable automatic checks for declared feeds of all API consumers.\n" : "Check this mod's declared feed once.\n") +
                "Requests expose your IP address and requested feed path to GitHub hosts and validated GitHub redirects. No saves, profile, machine ID or full inventory is sent.\n" +
                "Supported hosts: github.com, raw.githubusercontent.com, objects.githubusercontent.com, release-assets.githubusercontent.com.\n" +
                "Selected feed host: " + FeedHost(mod) + "\n" +
                "Automatic checks repeat at most every six hours after success. Disabling stops future checks; in-flight checks may finish.\n" +
                "Press the same button again to confirm. Select another mod or close to cancel. No downloads or installations.";
        var status = _service.Status(mod);
        var label = !_service.Enabled ? "Disabled globally" : status.State switch
        {
            ModUpdateState.NoSource => "No update source",
            ModUpdateState.NotChecked => "Not checked",
            ModUpdateState.Checking => "Checking",
            ModUpdateState.Current => "Up to date at last successful check",
            ModUpdateState.Available => "Update available at last successful check",
            ModUpdateState.InstalledAhead => "Installed version ahead; no downgrade recommended",
            ModUpdateState.Invalid => "Invalid feed or unsupported source",
            ModUpdateState.RateLimited => "Rate limited; retry delayed",
            _ => "Check failed; not an up-to-date result"
        };
        var result = "Update check: " + label + "\nAutomatic checks: " + (Automatic ? "on" : "off") + "\n";
        if (status.LastSuccess != null)
            result += "Last successful feed: " + status.LastSuccess.Version + " at " + status.CheckedAt!.Value.ToString("u") +
                (now - status.CheckedAt.Value >= TimeSpan.FromHours(6) || status.State is ModUpdateState.Failed or ModUpdateState.RateLimited or ModUpdateState.Invalid ? " (stale)" : " (cached)") + "\n";
        if (status.RetryAt > now) result += "Next permitted check: " + status.RetryAt.Value.ToString("u") + "\n";
        return result + "A newer release does not prove API, game or save compatibility.\n";
    }
    private static string FeedHost(ModInformation mod) => ModUpdateHosts.Allowed(mod.Metadata?.UpdateUrl ?? "") ? new Uri(mod.Metadata!.UpdateUrl!).IdnHost : "none supported";
    internal bool CanCheck(ModInformation mod) => _service.Enabled && ModUpdateHosts.Allowed(mod.Metadata?.UpdateUrl ?? "");
    internal bool OpenRelease(ModInformation mod, Action<string> open)
    {
        var status = _service.Status(mod);
        // Last-success data remains readable after a failure, but only current available results offer a release action.
        if (status.State != ModUpdateState.Available || status.LastSuccess == null || !ModMetadataCodec.IsPublicHttpsUrl(status.LastSuccess.ReleaseUrl)) return false;
        open(status.LastSuccess.ReleaseUrl); return true;
    }
    internal string? ReleaseHost(ModInformation mod)
    {
        string? host = null;
        OpenRelease(mod, url => host = new Uri(url).IdnHost);
        return host;
    }
}
