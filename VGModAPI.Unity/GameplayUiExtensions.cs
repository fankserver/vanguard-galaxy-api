using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace VGModAPI.Unity;

/// <summary>A consumer-owned UI parent within one gameplay UI lifetime. Main-thread only.</summary>
public sealed class GameplayUiContainer : IDisposable
{
    private readonly Func<RectTransform?> _root;
    private readonly Action _dispose;
    public GameplayUiSnapshot Host { get; }
    public string PluginId { get; }
    public string LocalId { get; }
    public bool IsValid => _root() != null;
    /// <summary>
    /// Parent consumer-created content here with SetParent(Root, false). Throws after revocation.
    /// Do not reparent, destroy, resize or persist this API-owned root; manage its children instead.
    /// </summary>
    public RectTransform Root => _root() ?? throw new ObjectDisposedException(nameof(GameplayUiContainer));
    internal GameplayUiContainer(GameplayUiSnapshot host, string pluginId, string localId, Func<RectTransform?> root, Action dispose)
    { Host = host; PluginId = pluginId; LocalId = localId; _root = root; _dispose = dispose; }
    /// <summary>Immediately revokes access and disables the root; Unity destroys it and its children.</summary>
    public void Dispose() => _dispose();
}

/// <summary>Optional typed Unity integration; no native game components are exposed.</summary>
public static class GameplayUiExtensions
{
    internal delegate GameplayUiContainerStatus ContainerFactory(GameplayUiSnapshot host, string pluginId, string localId, out GameplayUiContainer? container);
    private sealed class Source
    {
        internal readonly ContainerFactory Factory;
        internal Source(ContainerFactory factory) { Factory = factory; }
    }
    private static readonly ConditionalWeakTable<IGameplayUiService, Source> Sources = new();

    /// <summary>
    /// Creates a full-stretch, non-interactive parent on the expected live host. Refusal leaves container null.
    /// Provider/local identities must be unique within a host. At most 64 containers may coexist.
    /// Use the shared Hud service for launchers, not fixed-position icons on this surface.
    /// </summary>
    public static GameplayUiContainerStatus CreateContainer(this IGameplayUiService service, GameplayUiSnapshot host,
        string pluginId, string localId, out GameplayUiContainer? container)
    {
        if (service == null) throw new ArgumentNullException(nameof(service));
        // Check main-thread access even when the adapter is unavailable or stopped.
        var availability = service.Availability;
        if (host == null) throw new ArgumentNullException(nameof(host));
        _ = new RecipeId(pluginId, localId);
        container = null;
        if (!availability.IsAvailable || !Sources.TryGetValue(service, out var source)) return GameplayUiContainerStatus.Unavailable;
        return source.Factory(host, pluginId, localId, out container);
    }

    internal static void Bind(IGameplayUiService service, ContainerFactory factory) => Sources.Add(service, new Source(factory));
    internal static void Unbind(IGameplayUiService service) => Sources.Remove(service);
}
