using System;

namespace VGModAPI.Core;

internal sealed class ModUpdatePresenter
{
    private readonly ModUpdateService _service;
    internal ModUpdatePresenter(ModUpdateService service) => _service = service;
    internal void Sync(System.Collections.Generic.IReadOnlyList<ModInformation> mods) => _service.Sync(mods);
    internal bool Check(ModInformation mod) => CanCheck(mod) && _service.Request(mod.PluginId);
    internal string Text(ModInformation mod, DateTimeOffset now)
    {
        var status = _service.Status(mod);
        if (!_service.Enabled) return "Update checking unavailable.\n";
        return status.State switch
        {
            ModUpdateState.NoSource => "This mod does not provide update checks.\n",
            ModUpdateState.NotChecked => "Waiting to check for updates...\n",
            ModUpdateState.Checking => "Checking for updates...\n",
            ModUpdateState.Current => "Up to date.\n",
            ModUpdateState.Available => "Update available: " + status.LastSuccess?.Version + "\n",
            ModUpdateState.InstalledAhead => "Installed version is newer than the published release.\n",
            ModUpdateState.RateLimited => "Couldn't check for updates. Will retry later.\n",
            _ => "Couldn't check for updates.\n"
        };
    }
    internal bool CanCheck(ModInformation mod) => _service.Enabled && ModUpdateHosts.Allowed(mod.Metadata?.UpdateUrl ?? "");
    internal bool OpenRelease(ModInformation mod, Action<string> open)
    {
        var status = _service.Status(mod);
        // Only an available result offers a download-page action; the browser handles the page.
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
