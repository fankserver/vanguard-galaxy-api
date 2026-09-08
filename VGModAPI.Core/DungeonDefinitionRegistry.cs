using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

internal sealed class DungeonDefinitionRegistry
{
    private readonly Dictionary<DungeonDefinitionId, Entry> _definitions = new();
    private readonly Func<string, bool> _crew, _item, _faction;
    internal DungeonDefinitionRegistry(Func<string, bool> crew, Func<string, bool> item, Func<string, bool> faction)
    { _crew = crew; _item = item; _faction = faction; }
    internal int Count => _definitions.Count;
    internal bool TryGet(DungeonDefinitionId id, out DungeonDefinition definition)
    {
        if (_definitions.TryGetValue(id, out var entry)) { definition = entry.Definition; return true; }
        definition = null!; return false;
    }
    internal IDisposable Register(DungeonDefinitionId id, DungeonDefinition definition)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (_definitions.ContainsKey(id)) throw new InvalidOperationException("Duplicate provider/local dungeon definition ID.");
        if (_definitions.Count >= 256) throw new InvalidOperationException("Dungeon definition limit reached.");
        ValidateCatalogs(definition);
        var entry = new Entry(this, id, definition); _definitions.Add(id, entry); return entry;
    }
    internal void ValidateCatalogs(DungeonDefinition definition)
    {
        if (definition.FactionId != null && !_faction(definition.FactionId)) throw new ArgumentException("Unknown native faction ID: " + definition.FactionId);
        foreach (var id in definition.Layout.Compartments.SelectMany(room => room.Defenders.Keys).Distinct(StringComparer.Ordinal))
            if (!_crew(id)) throw new ArgumentException("Unknown native crew ID: " + id);
        foreach (var choice in definition.Events.SelectMany(e => e.Choices))
        {
            if (choice.RequiredCrewId != null && !_crew(choice.RequiredCrewId)) throw new ArgumentException("Unknown native specialist ID: " + choice.RequiredCrewId);
            foreach (var loot in choice.Loot)
                if (!_item(loot.ItemId)) throw new ArgumentException("Unknown native item ID: " + loot.ItemId);
        }
    }
    private void Remove(Entry entry)
    {
        if (_definitions.TryGetValue(entry.Id, out var current) && ReferenceEquals(entry, current)) _definitions.Remove(entry.Id);
    }
    private sealed class Entry : IDisposable
    {
        private readonly DungeonDefinitionRegistry _owner;
        internal readonly DungeonDefinitionId Id;
        internal readonly DungeonDefinition Definition;
        internal Entry(DungeonDefinitionRegistry owner, DungeonDefinitionId id, DungeonDefinition definition) { _owner = owner; Id = id; Definition = definition; }
        public void Dispose() => _owner.Remove(this);
    }
}
