using System;
using System.Collections.Generic;
using System.Linq;
namespace VGModAPI.Core;

/// <summary>API-owned authored choices use the same registered provider validation as consumer requests.</summary>
internal sealed class DungeonPanelChoices : IDisposable
{
    private readonly DungeonPanelService _panel;
    private readonly DungeonContentService _content;
    private readonly Func<BoardingHandle, Guid?> _occurrence;
    private readonly Dictionary<string, IDisposable> _leases = new(StringComparer.Ordinal);
    private readonly string _identity = "vgmodapi.choices." + Guid.NewGuid().ToString("N");
    internal DungeonPanelChoices(DungeonPanelService panel, DungeonContentService content, Func<BoardingHandle, Guid?> occurrence)
    { _panel = panel; _content = content; _occurrence = occurrence; }
    internal void Refresh()
    {
        var view = _panel.Current;
        var id = view == null ? null : _occurrence(view.Target.Handle);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (id.HasValue)
        {
            foreach (var choice in _content.PanelChoices(id.Value))
            {
                var occurrenceId = id.Value; var eventId = choice.EventId; var choiceId = choice.ChoiceId;
                var eventKey = occurrenceId.ToString("N") + ":event:" + eventId;
                wanted.Add(eventKey);
                if (!_leases.ContainsKey(eventKey))
                    _leases.Add(eventKey, _panel.RegisterSection(_identity, Guid.NewGuid().ToString("N"), snapshot =>
                        _occurrence(snapshot.Target.Handle) == occurrenceId && _content.PanelChoices(occurrenceId, eventId).Count > 0
                            ? new DungeonPanelSection("Encounter choice", choice.EventText) : null, -101));
                var key = occurrenceId.ToString("N") + ":choice:" + eventId.Length + ":" + eventId + choiceId; wanted.Add(key);
                if (_leases.ContainsKey(key)) continue;
                _leases.Add(key, _panel.RegisterAction(_identity, Guid.NewGuid().ToString("N"), snapshot =>
                {
                    if (_occurrence(snapshot.Target.Handle) != occurrenceId) return null;
                    var available = _content.PanelChoices(occurrenceId, eventId, choiceId).Count > 0;
                    return available ? new DungeonPanelAction(Short(choice.ChoiceText, 128), choice.ChoiceText) : null;
                }, snapshot =>
                {
                    if (_occurrence(snapshot.Target.Handle) == occurrenceId) _content.ChooseFromPanel(occurrenceId, eventId, choiceId);
                }, -100));
            }
        }
        foreach (var key in _leases.Keys.Where(key => !wanted.Contains(key)).ToArray()) { _leases[key].Dispose(); _leases.Remove(key); }
    }
    private static string Short(string text, int limit) => text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
    public void Dispose() { foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear(); }
}
