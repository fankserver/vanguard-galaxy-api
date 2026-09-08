using System;
using System.Collections.Generic;

namespace VGModAPI;

/// <summary>One observed Forge screen lifetime, never a saved identity or a native object.</summary>
public sealed class ForgeViewHandle : IEquatable<ForgeViewHandle>
{
    public Guid SessionId { get; }
    public Guid InstanceId { get; }
    public ForgeViewHandle(Guid sessionId, Guid instanceId)
    {
        if (sessionId == Guid.Empty || instanceId == Guid.Empty) throw new ArgumentException("Empty view identity.");
        SessionId = sessionId; InstanceId = instanceId;
    }
    public bool Equals(ForgeViewHandle? other) => other != null && SessionId == other.SessionId && InstanceId == other.InstanceId;
    public override bool Equals(object? obj) => Equals(obj as ForgeViewHandle);
    public override int GetHashCode() => HashCode.Combine(SessionId, InstanceId);
}

/// <summary>Presentation copied from the selected UI, separate from recipe economics. No native asset escapes.</summary>
public sealed class ForgeSelectionPresentation
{
    public string DisplayName { get; }
    public bool HasNativeIcon { get; }
    public ForgeSelectionPresentation(string displayName, bool hasNativeIcon)
    { DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName)); HasNativeIcon = hasNativeIcon; }
}

public sealed class ForgeSelectionSnapshot
{
    public ForgeViewHandle View { get; }
    public RecipeStationHandle Station { get; }
    public RecipeId ParentRecipe { get; }
    public RecipeId SelectedRecipe { get; }
    public IReadOnlyList<RecipeId> AvailableVariants { get; }
    public int Batches { get; }
    public long Revision { get; }
    public ForgeSelectionPresentation Presentation { get; }
    public ForgeSelectionSnapshot(ForgeViewHandle view, RecipeStationHandle station, RecipeId parentRecipe,
        RecipeId selectedRecipe, IEnumerable<RecipeId> availableVariants, int batches, long revision, ForgeSelectionPresentation presentation)
    {
        View = view ?? throw new ArgumentNullException(nameof(view)); Station = station ?? throw new ArgumentNullException(nameof(station));
        if (view.SessionId != station.SessionId || batches < 0 || revision < 0) throw new ArgumentException("Invalid selection context.");
        ParentRecipe = parentRecipe ?? throw new ArgumentNullException(nameof(parentRecipe));
        SelectedRecipe = selectedRecipe ?? throw new ArgumentNullException(nameof(selectedRecipe));
        AvailableVariants = RecipeValues.Copy(availableVariants, 256);
        var identities = new HashSet<RecipeId>(AvailableVariants);
        if (identities.Count != AvailableVariants.Count || !identities.Contains(selectedRecipe)) throw new ArgumentException("Invalid selected variant group.");
        Batches = batches; Revision = revision; Presentation = presentation ?? throw new ArgumentNullException(nameof(presentation));
    }
}

public sealed class ForgeSelectionChange
{
    public ForgeSelectionSnapshot? Previous { get; }
    public ForgeSelectionSnapshot? Current { get; }
    public ForgeSelectionChange(ForgeSelectionSnapshot? previous, ForgeSelectionSnapshot? current)
    { Previous = previous; Current = current; }
}

public sealed class ForgeActionPresentation
{
    public string Label { get; }
    public string Tooltip { get; }
    public bool Enabled { get; }
    /// <summary>Use the already-rendered selection icon inside this Forge action, not a general HUD asset export.</summary>
    public bool UseSelectionIcon { get; }
    public ForgeActionPresentation(string label, string tooltip = "", bool enabled = true, bool useSelectionIcon = false)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 64) throw new ArgumentException("Action label must contain 1–64 characters.", nameof(label));
        if (tooltip == null || tooltip.Length > 512) throw new ArgumentException("Tooltip exceeds 512 characters.", nameof(tooltip));
        Label = label; Tooltip = tooltip; Enabled = enabled; UseSelectionIcon = useSelectionIcon;
    }
}

public interface IForgeActionRegistration : IDisposable
{
    void Update(ForgeActionPresentation presentation);
}
public enum ForgeNavigationStatus { Selected, Unavailable, NotAtStation, RecipeUnavailable, Busy, Uncertain }

/// <summary>Main-thread Forge selection/actions. Registrations survive window/session replacement until disposed.</summary>
public interface IForgeUi
{
    ForgeSelectionSnapshot? Current { get; }
    IDisposable Subscribe(string pluginId, Action<ForgeSelectionChange> callback);
    IForgeActionRegistration RegisterAction(string pluginId, string localId, ForgeActionPresentation presentation,
        Action<ForgeSelectionSnapshot> callback, int order = 0);
    ForgeNavigationStatus Open(RecipeId recipe);
}
