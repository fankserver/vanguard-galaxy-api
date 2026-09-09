using System;
using System.Collections.Generic;

namespace VGModAPI;

public enum HudPresentationKind { Item, RefinedMaterial, ForgeRecipe }
/// <summary>A presentation request, not a native asset. Rendering can use native preview/icon/tooltip behavior.</summary>
public sealed class HudPresentation
{
    public string ProviderId { get; }
    public string LocalId { get; }
    public HudPresentationKind Kind { get; }
    public int TooltipCount { get; }
    public HudPresentation(string providerId, string localId, HudPresentationKind kind, int tooltipCount = 1)
    {
        ProviderId = RecipeValues.Identity(providerId, nameof(providerId)); LocalId = RecipeValues.Identity(localId, nameof(localId));
        if (!Enum.IsDefined(typeof(HudPresentationKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (tooltipCount < 1) throw new ArgumentOutOfRangeException(nameof(tooltipCount));
        Kind = kind; TooltipCount = tooltipCount;
    }
}
public sealed class HudRow
{
    public string Id { get; }
    public string Label { get; }
    public string Detail { get; }
    public string Tooltip { get; }
    public HudPresentation? Presentation { get; }
    public bool Clickable { get; }
    public HudIngredientAmounts? IngredientAmounts { get; private set; }
    /// <summary>Reusable native-style ingredient row. Unknown availability is not displayed as zero.</summary>
    public static HudRow Ingredient(string id, string label, double required, double? available,
        string tooltip = "", HudPresentation? presentation = null, bool clickable = false)
        => new HudRow(id, label, "", tooltip, presentation, clickable)
        { IngredientAmounts = new HudIngredientAmounts(required, available) };
    public HudRow(string id, string label, string detail = "", string tooltip = "", HudPresentation? presentation = null, bool clickable = false)
    {
        Id = RecipeValues.Identity(id, nameof(id)); Label = HudText.Check(label, 256); Detail = HudText.Check(detail, 256); Tooltip = HudText.Check(tooltip, 1024);
        if (Label.Length == 0 && presentation == null) throw new ArgumentException("A label or presentation name is required.");
        Presentation = presentation; Clickable = clickable;
    }
}
public sealed class HudPanel
{
    public string Title { get; }
    public HudPresentation? Presentation { get; }
    public IReadOnlyList<HudRow> Rows { get; }
    public bool Closable { get; }
    public HudPanel(string title, IEnumerable<HudRow> rows, HudPresentation? presentation = null, bool closable = true)
    {
        Title = HudText.Check(title, 256); Rows = RecipeValues.Copy(rows, 32); Presentation = presentation; Closable = closable;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows) if (!ids.Add(row.Id)) throw new ArgumentException("Duplicate HUD row identity.");
    }
}
/// <summary>Each corner is one shared launcher layout across all providers.</summary>
public enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }
/// <summary>Semantic game visuals; native sprite names and atlas coordinates are adapter details.</summary>
public enum HudIcon { Storage, Refinery }
public sealed class HudButton
{
    public string Label { get; }
    public string Tooltip { get; }
    public bool Enabled { get; }
    public HudCorner Corner { get; }
    public HudIcon? Icon { get; }
    public HudButton(string label, string tooltip = "", bool enabled = true)
        : this(label, HudCorner.BottomRight, null, tooltip, enabled) { }
    /// <summary>Corner controls standalone launchers only; panel actions remain in their panel footer.</summary>
    public HudButton(string label, HudCorner corner, HudIcon? icon = null, string tooltip = "", bool enabled = true)
    {
        if (!Enum.IsDefined(typeof(HudCorner), corner)) throw new ArgumentOutOfRangeException(nameof(corner));
        if (icon.HasValue && !Enum.IsDefined(typeof(HudIcon), icon.Value)) throw new ArgumentOutOfRangeException(nameof(icon));
        Corner = corner; Icon = icon;
        Label = HudText.Check(label, 64); Tooltip = HudText.Check(tooltip, 1024); Enabled = enabled;
        if (string.IsNullOrWhiteSpace(Label)) throw new ArgumentException("Button label required.");
    }
}
public enum HudInteractionKind { Button, ClosePanel, Row }
public sealed class HudInteraction
{
    public Guid SessionId { get; }
    public HudInteractionKind Kind { get; }
    public string? RowId { get; }
    public long Revision { get; }
    public HudInteraction(Guid sessionId, HudInteractionKind kind, string? rowId, long revision)
    { SessionId = sessionId; Kind = kind; RowId = rowId; Revision = revision; }
}
public interface IHudRegistration : IDisposable
{
    /// <summary>Replace transient content; null hides the respective surface. No save callbacks are needed.</summary>
    void Update(HudButton? button, HudPanel? panel);
}
/// <summary>Bounded shared HUD buttons and information panels, not a window/widget framework. Main-thread only.</summary>
public interface IHudService : IServiceStatus
{
    bool Visible { get; }
    IHudRegistration Register(string pluginId, string localId, Action<HudInteraction> callback, int order = 0);
}
internal static class HudText
{
    internal static string Check(string text, int max)
    { if (text == null || text.Length > max) throw new ArgumentException("HUD text exceeds its bound."); return text; }
}
