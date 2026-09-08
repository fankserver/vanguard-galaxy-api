using System;

namespace VGModAPI;

public enum DungeonPanelOpenStatus { Opened, Unavailable, StaleTarget, Busy }

/// <summary>Independent adapter availability; an enabled control is never command authorization.</summary>
public sealed class DungeonPanelCapabilities
{
    public bool Opening { get; }
    public bool StatusSections { get; }
    public bool ContextualActions { get; }
    public DungeonPanelCapabilities(bool opening, bool statusSections, bool contextualActions)
    { Opening = opening; StatusSections = statusSections; ContextualActions = contextualActions; }
}

/// <summary>One panel lifetime and revision, containing only already-observed target information.</summary>
public sealed class DungeonPanelSnapshot
{
    public Guid ViewId { get; }
    public long Revision { get; }
    public BoardingTargetSnapshot Target { get; }
    public BoardingOperationSnapshot? Operation { get; }
    public DungeonPanelSnapshot(Guid viewId, long revision, BoardingTargetSnapshot target, BoardingOperationSnapshot? operation)
    {
        if (viewId == Guid.Empty || revision < 1) throw new ArgumentException("Invalid panel identity or revision.");
        Target = target ?? throw new ArgumentNullException(nameof(target));
        if (operation != null && (!operation.Target.Equals(target.Handle) || target.Operation == null || !target.Operation.Equals(operation.Handle))) throw new ArgumentException("Operation does not belong to the selected target.");
        ViewId = viewId; Revision = revision; Operation = operation;
    }
}

public sealed class DungeonPanelSection
{
    public string Title { get; }
    public string Text { get; }
    public DungeonPanelSection(string title, string text)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 128) throw new ArgumentException("Section title must contain 1–128 characters.", nameof(title));
        if (text == null || text.Length > 4096) throw new ArgumentException("Section text exceeds 4096 characters.", nameof(text));
        Title = title; Text = text;
    }
}

public sealed class DungeonPanelAction
{
    public string Label { get; }
    public string Tooltip { get; }
    public bool Enabled { get; }
    public DungeonPanelAction(string label, string tooltip = "", bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 128) throw new ArgumentException("Action label must contain 1–128 characters.", nameof(label));
        if (tooltip == null || tooltip.Length > 1024) throw new ArgumentException("Action tooltip exceeds 1024 characters.", nameof(tooltip));
        Label = label; Tooltip = tooltip; Enabled = enabled;
    }
}

/// <summary>Main-thread panel presentation. Null presentation hides a contribution for the current phase.
/// Registrations are disposed by the contributor; view callbacks and activation identities expire on close or session change.
/// Presenters are reevaluated on activation; gameplay commands must still perform their own validation.</summary>
public interface IDungeonPanelApi
{
    DungeonPanelCapabilities Capabilities { get; }
    DungeonPanelSnapshot? Current { get; }
    DungeonPanelOpenStatus Open(BoardingHandle target);
    IDisposable RegisterSection(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelSection?> present, int order = 0);
    IDisposable RegisterAction(string pluginId, string localId, Func<DungeonPanelSnapshot, DungeonPanelAction?> present,
        Action<DungeonPanelSnapshot> activate, int order = 0);
}
