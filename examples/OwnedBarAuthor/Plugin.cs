using System;
using BepInEx;
using VGModAPI;

namespace OwnedBarAuthor;

[BepInPlugin(Id, "Owned bar author example", "0.1.0")]
[BepInDependency(ModApi.PluginId, "0.1.25")]
public sealed class Plugin : BaseUnityPlugin
{
#if BAR_AUTHOR_B
    public const string Id = "vg-bar-author-b";
#else
    public const string Id = "vg-bar-author-a";
#endif
    private IBarProvider? _provider;
    public IBarProvider Provider => _provider ?? throw new InvalidOperationException("Register the author first.");
    public int Interactions { get; private set; }

    public BarResult Register(string station, string local, string seed)
    {
        if (_provider == null)
            _provider = (ModApi.Bars ?? throw new InvalidOperationException("Enable owned bars."))
                .AcquireProvider(this).Provider ?? throw new InvalidOperationException("Author authentication refused.");
        return _provider.Register(new BarPatronDefinition(local, station, "Contact " + Id,
            "Independently authored contact", seed, BarPatronRetention.Persistent), _ => Interactions++);
    }

    public BarResult Configure(string station, BarRosterOwnership mode)
        => Provider.ConfigureStation(station, mode);
    public BarResult Place(Guid session, string local) => Provider.Place(session, local);
    public void Release() { _provider?.Dispose(); _provider = null; }
    private void OnDestroy() => Release();
}
