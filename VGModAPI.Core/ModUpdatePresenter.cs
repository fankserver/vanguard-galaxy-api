using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class ModUpdatePresenter
{
    private readonly ModUpdateService _service;
    internal ModUpdatePresenter(ModUpdateService service) => _service = service;
    internal void Sync(IReadOnlyList<ModInformation> mods) => _service.Sync(mods);
    internal bool Check(ModInformation mod) => CanCheck(mod) && _service.Request(mod.PluginId);
    internal void CheckAll(IReadOnlyList<ModInformation> mods)
    {
        Sync(mods);
        foreach (var mod in mods) Check(mod);
    }
    internal ModUpdateState State(ModInformation mod) => _service.Enabled ? _service.Status(mod).State : ModUpdateState.Failed;
    internal string Label(ModInformation mod) => State(mod) switch
    {
        ModUpdateState.NoSource => "No update information",
        ModUpdateState.NotChecked => "Waiting to check",
        ModUpdateState.Checking => "Checking...",
        ModUpdateState.Current => "Up to date",
        ModUpdateState.Available => "Update available",
        ModUpdateState.InstalledAhead => "Newer than published",
        _ => "Update check failed"
    };
    internal int AvailableCount(IReadOnlyList<ModInformation> mods) => mods.Count(mod => State(mod) == ModUpdateState.Available);
    internal string Summary(IReadOnlyList<ModInformation> mods)
    {
        if (mods.Count == 0) return "No mods to show";
        var parts = new List<string> { mods.Count + (mods.Count == 1 ? " mod" : " mods") };
        var available = AvailableCount(mods);
        var failed = mods.Count(mod => State(mod) is ModUpdateState.Invalid or ModUpdateState.Failed or ModUpdateState.RateLimited);
        var pending = mods.Count(mod => State(mod) is ModUpdateState.NotChecked or ModUpdateState.Checking);
        if (available > 0) parts.Add(available + (available == 1 ? " update available" : " updates available"));
        if (failed > 0) parts.Add(failed + (failed == 1 ? " update check failed" : " update checks failed"));
        if (pending > 0) parts.Add("Checking for updates...");
        if (mods.All(mod => State(mod) == ModUpdateState.Current)) parts.Add("All up to date");
        return string.Join("  |  ", parts);
    }
    internal string Text(ModInformation mod, DateTimeOffset now)
    {
        var status = _service.Status(mod);
        var latest = status.LastSuccess?.Version.ToString() ?? "Unknown";
        var result = "Installed: " + mod.InstalledVersion + "    Latest: " + latest + "\n" + Label(mod);
        if (!_service.Enabled) return result + " - update checking unavailable.";
        if (status.State is ModUpdateState.Failed or ModUpdateState.Invalid or ModUpdateState.RateLimited)
            result += status.RetryAt > now ? ". Will retry later." : ". Try again.";
        return result;
    }
    internal bool CanCheck(ModInformation mod) => _service.Enabled && ModUpdateHosts.Allowed(mod.Metadata?.UpdateUrl ?? "");
    internal bool CanRequest(ModInformation mod, DateTimeOffset now) => CanCheck(mod) &&
        State(mod) != ModUpdateState.Checking && !(_service.Status(mod).RetryAt > now);
    internal bool OpenRelease(ModInformation mod, Action<string> open)
    {
        var status = _service.Status(mod);
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
