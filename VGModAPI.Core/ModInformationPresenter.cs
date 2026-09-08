using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VGModAPI.Core;

internal sealed class ModInformationPresenter
{
    private readonly IModInformationCatalog _catalog;
    internal ModInformationPresenter(IModInformationCatalog catalog) => _catalog = catalog;
    internal IReadOnlyList<ModInformation> Rows { get; private set; } = Array.Empty<ModInformation>();
    internal string? RefreshWarning { get; private set; }
    internal string? SelectedId { get; private set; }
    internal ModInformation? Selected => Rows.FirstOrDefault(row => row.PluginId == SelectedId);

    internal void Open()
    {
        try { _catalog.Refresh(); RefreshWarning = null; }
        catch (Exception) { RefreshWarning = "Couldn't refresh the mod list. Showing the previous list."; }
        Rows = _catalog.Snapshot;
        if (Selected == null) SelectedId = Rows.FirstOrDefault()?.PluginId;
    }

    internal bool Select(string id)
    {
        if (!Rows.Any(row => row.PluginId == id)) return false;
        SelectedId = id; return true;
    }

    internal static string DisplayName(ModInformation row) => PlainText(row.Name, 160, false);
    internal static string MetadataNotice(ModInformation row) => row.MetadataStatus switch
    {
        ModMetadataStatus.Missing => "No optional author metadata.",
        ModMetadataStatus.Invalid => "Optional author metadata is invalid; installed identity is still shown.",
        ModMetadataStatus.Unreadable => "Optional author metadata could not be read; installed identity is still shown.",
        _ => "Author-provided metadata; not a compatibility guarantee."
    };
    internal static string UpdateNotice(ModInformation row) => row.Metadata?.UpdateUrl == null ? "No update source." : "Not checked.";

    internal bool TryProjectDestination(out string host)
    {
        host = "";
        var url = Selected?.Metadata?.ProjectUrl;
        if (url == null || !ModMetadataCodec.IsPublicHttpsUrl(url)) return false;
        // Revalidate independently of author-controlled display text before any browser action.
        host = new Uri(url).IdnHost;
        return true;
    }

    // Invoke only from an explicit UI click. Revalidate the selected link at the point of opening.
    internal bool OpenProject(Action<string> open)
    {
        if (!TryProjectDestination(out _)) return false;
        open(Selected!.Metadata!.ProjectUrl!); return true;
    }

    internal string Details()
    {
        var text = new StringBuilder();
        if (RefreshWarning != null) text.Append(RefreshWarning).Append('\n');
        var row = Selected;
        if (row == null) text.Append("No mods to show.\n");
        else
        {
            text.Append(DisplayName(row)).Append('\n');
            if (row.Metadata != null)
            {
                if (row.Metadata.Author != null) text.Append("by ").Append(PlainText(row.Metadata.Author, 256, false)).Append('\n');
                if (row.Metadata.Description != null) text.Append('\n').Append(PlainText(row.Metadata.Description, 4096, true)).Append('\n');
            }
        }
        return text.ToString();
    }

    internal static string PlainText(string text, int limit, bool multiline)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        var result = new StringBuilder(Math.Min(limit, 256));
        foreach (var c in text)
        {
            // Keep ordinary RTL letters and joining characters. Remove explicit direction overrides/isolates,
            // which can otherwise make an author label impersonate adjacent UI or reverse a destination.
            if (c is '\u061c' or '\u200e' or '\u200f' || c >= '\u202a' && c <= '\u202e' || c >= '\u2066' && c <= '\u2069') continue;
            if (char.GetUnicodeCategory(c) == UnicodeCategory.Control)
            {
                if (c is '\r' or '\n' or '\t') result.Append(multiline && c == '\n' ? '\n' : ' ');
                if (result.Length > limit) break;
                continue;
            }
            result.Append(c);
            if (result.Length > limit) break;
        }
        if (result.Length <= limit) return result.ToString();
        var suffix = new string('.', Math.Min(3, limit));
        var take = limit - suffix.Length;
        if (take > 0 && char.IsHighSurrogate(result[take - 1])) --take;
        return result.ToString(0, take) + suffix;
    }
}
