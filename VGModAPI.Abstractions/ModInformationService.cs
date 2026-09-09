using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI;

public enum ModInventoryStatus { NotCollected, Partial, Current, RefreshFailed, Stopped }

/// <summary>Immutable inventory within the declared-consumer scope, with explicit freshness.</summary>
public sealed class ModInventorySnapshot
{
    public ModInventoryStatus Status { get; }
    /// <summary>May contain the last successful rows after refresh failure; only Current claims a fresh post-startup snapshot.</summary>
    public IReadOnlyList<ModInformation> Entries { get; }
    public string Detail { get; }

    public ModInventorySnapshot(ModInventoryStatus status, IEnumerable<ModInformation> entries, string detail = "")
    {
        if (!Enum.IsDefined(typeof(ModInventoryStatus), status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        var copy = entries.Select(row => row ?? throw new ArgumentException("Null inventory entry.", nameof(entries)))
            .OrderBy(row => row.PluginId, StringComparer.Ordinal).ToArray();
        if (copy.Select(row => row.PluginId).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Duplicate plugin identity.", nameof(entries));
        if (status == ModInventoryStatus.NotCollected && copy.Length != 0)
            throw new ArgumentException("An uncollected inventory has no entries.", nameof(entries));
        Status = status; Entries = Array.AsReadOnly(copy);
        Detail = detail ?? throw new ArgumentNullException(nameof(detail));
    }
}

/// <summary>Main-thread inventory access, independent of inspected game hooks and native menu health.</summary>
public interface IModInformationService : IServiceStatus
{
    IServiceStatus Menu { get; }
    ModInventorySnapshot Inventory { get; }
    /// <summary>During loader startup the result is Partial, not a complete post-startup snapshot.</summary>
    ModInventorySnapshot Refresh();
    event Action<ModInventorySnapshot>? InventoryChanged;
}
