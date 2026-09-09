using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VGModAPI;

/// <summary>Loader presence only: not initialization success, gameplay health or update status.</summary>
public sealed class ModInformation
{
    public ModInformation(string pluginId, string name, Version installedVersion,
        IEnumerable<ModDependencyInformation> dependencies, ModAuthorMetadata? metadata,
        ModMetadataStatus metadataStatus)
    {
        PluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        Name = name ?? throw new ArgumentNullException(nameof(name));
        InstalledVersion = installedVersion ?? throw new ArgumentNullException(nameof(installedVersion));
        Dependencies = new ReadOnlyCollection<ModDependencyInformation>((dependencies ?? throw new ArgumentNullException(nameof(dependencies))).ToArray());
        Metadata = metadata;
        MetadataStatus = metadataStatus;
    }
    public string PluginId { get; }
    public string Name { get; }
    public Version InstalledVersion { get; }
    public IReadOnlyList<ModDependencyInformation> Dependencies { get; }
    public ModAuthorMetadata? Metadata { get; }
    public ModMetadataStatus MetadataStatus { get; }
}

public sealed class ModDependencyInformation
{
    public ModDependencyInformation(string pluginId, Version? minimumVersion, bool hardDependency)
    { PluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId)); MinimumVersion = minimumVersion; HardDependency = hardDependency; }
    public string PluginId { get; }
    public Version? MinimumVersion { get; }
    public bool HardDependency { get; }
}

public enum ModMetadataStatus { Missing, Available, Invalid, Unreadable }

/// <summary>Optional author data. Installed identity and version always come from the loader.</summary>
public sealed class ModAuthorMetadata
{
    public ModAuthorMetadata(string? author, string? description, string? projectUrl, string? updateUrl, string channel)
    { Author = author; Description = description; ProjectUrl = projectUrl; UpdateUrl = updateUrl; Channel = channel; }
    public string? Author { get; }
    public string? Description { get; }
    public string? ProjectUrl { get; }
    public string? UpdateUrl { get; }
    public string Channel { get; }
}
