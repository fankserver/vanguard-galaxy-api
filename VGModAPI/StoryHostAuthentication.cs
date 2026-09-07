using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using VGModAPI.Core;

namespace VGModAPI;

/// <summary>
/// Resolves a caller-supplied plugin instance to the identity the HOST recorded for it. The story
/// module never derives a provider from a caller-supplied string, and never from the argument alone:
/// the instance must be a plugin BepInEx actually loaded, and the assembly it was loaded from must be
/// the assembly that called the API. That is an ordinary-use boundary, not a sandbox: an assembly
/// declaring several plugins can still acquire any of its own, and reflection is not prevented.
/// </summary>
internal static class StoryHostAuthentication
{
    internal static StoryHostPlugin? Resolve(object pluginInstance, Assembly callingAssembly)
    {
        if (pluginInstance == null || callingAssembly == null) return null;
        var info = Chainloader.PluginInfos.Values.FirstOrDefault(candidate => ReferenceEquals(candidate?.Instance, pluginInstance));
        if (info?.Metadata?.GUID == null) return null;
        // The assembly the host loaded this plugin FROM, not one the caller can nominate.
        var assembly = pluginInstance.GetType().Assembly;
        if (!ReferenceEquals(assembly, callingAssembly)) return null;
        return new StoryHostPlugin(info.Metadata.GUID, assembly);
    }
}
