using System;
using System.Collections.Generic;
using System.Linq;
namespace VGModAPI.Core;

/// <summary>API-owned authored choices use the same registered provider validation as consumer requests.</summary>
internal sealed class DungeonPanelChoices : IDisposable
{
    private readonly DungeonPanelService _panel;
    private readonly DungeonContentService _content;
    private readonly Func<BoardingHandle, Guid?> _dungeon;
    private readonly Dictionary<string, IDisposable> _leases = new(StringComparer.Ordinal);
    private Guid? _lastView, _lastDungeon;
    private long _lastRevision;
    private (object? Dungeon, object? Provider, object? Definition) _lastToken;
    private readonly string _identity = "vgmodapi.choices." + Guid.NewGuid().ToString("N");
    internal DungeonPanelChoices(DungeonPanelService panel, DungeonContentService content, Func<BoardingHandle, Guid?> dungeon)
    { _panel = panel; _content = content; _dungeon = dungeon; }
    internal void Refresh()
    {
        var view = _panel.Current;
        var id = view == null ? null : _dungeon(view.Target.Handle);
        if (view != null && id.HasValue && _content.PanelBusy) return;
        var token = id.HasValue ? _content.PanelToken(id.Value) : default;
        if (_lastView == view?.ViewId && _lastRevision == (view?.Revision ?? 0) && _lastDungeon == id && _lastToken.Equals(token)) return;
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        if (id.HasValue)
        {
            foreach (var choice in _content.PanelChoices(id.Value))
            {
                var dungeonId = id.Value; var eventId = choice.EventId; var choiceId = choice.ChoiceId;
                var eventKey = dungeonId.ToString("N") + ":event:" + eventId;
                wanted.Add(eventKey);
                if (!_leases.ContainsKey(eventKey))
                    _leases.Add(eventKey, _panel.RegisterSection(_identity, Guid.NewGuid().ToString("N"), snapshot =>
                        _dungeon(snapshot.Target.Handle) == dungeonId && _content.PanelChoices(dungeonId, eventId).Count > 0
                            ? new DungeonPanelSection("Encounter choice", choice.EventText) : null, -101));
                var key = dungeonId.ToString("N") + ":choice:" + eventId.Length + ":" + eventId + choiceId; wanted.Add(key);
                if (_leases.ContainsKey(key)) continue;
                _leases.Add(key, _panel.RegisterAction(_identity, Guid.NewGuid().ToString("N"), snapshot =>
                {
                    if (_dungeon(snapshot.Target.Handle) != dungeonId) return null;
                    var available = _content.PanelChoices(dungeonId, eventId, choiceId).Count > 0;
                    return available ? new DungeonPanelAction(Short(choice.ChoiceText, 128), choice.ChoiceText) : null;
                }, snapshot =>
                {
                    if (_dungeon(snapshot.Target.Handle) == dungeonId) _content.ChooseFromPanel(dungeonId, eventId, choiceId);
                }, -100));
            }
        }
        foreach (var key in _leases.Keys.Where(key => !wanted.Contains(key)).ToArray()) { _leases[key].Dispose(); _leases.Remove(key); }
        _lastView = view?.ViewId; _lastRevision = view?.Revision ?? 0; _lastDungeon = id; _lastToken = token;
    }
    private static string Short(string text, int limit) => text.Length <= limit ? text : text.Substring(0, limit - 1) + "…";
    public void Dispose() { foreach (var lease in _leases.Values) lease.Dispose(); _leases.Clear(); _lastView = null; _lastDungeon = null; _lastRevision = 0; _lastToken = default; }
}
