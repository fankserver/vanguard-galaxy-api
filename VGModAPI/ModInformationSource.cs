using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using VGModAPI.Core;

namespace VGModAPI;

internal static class ModInformationSource
{
    // Called from Start / explicit main-thread refresh, never enumerated in the API's Awake.
    internal static IEnumerable<LoadedPluginInformation> Snapshot() => Chainloader.PluginInfos.Values
        .Select(plugin => new LoadedPluginInformation(plugin.Metadata.GUID, plugin.Metadata.Name,
            plugin.Metadata.Version, plugin.Location, plugin.Dependencies.Select(dependency =>
                new ModDependencyInformation(dependency.DependencyGUID, dependency.MinimumVersion,
                    (dependency.Flags & BepInDependency.DependencyFlags.HardDependency) != 0))))
        .ToArray();
}
