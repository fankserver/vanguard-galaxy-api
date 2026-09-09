using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VGModAPI;
namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Native fixture construction, not a consumer-facing crew provisioning API.
    private void SeedRewardCrew()
    {
        var player = SpGet(NativeType("Source.Player.GamePlayer"), "current")!;
        var donor = SpGet(player, "currentSpaceShip")!;
        var roster = (Dictionary<string, int>)SpGet(SpGet(donor, "crewData")!, "crew")!;
        Require(!roster.TryGetValue("Marine", out var existing) || existing == 0, "Reward fixture requires no existing Marines.");
        Require(roster.TryGetValue("Deckhand", out var deckhands) && deckhands >= 6, "Reward fixture requires six Deckhands to replace.");
        if (deckhands == 6) roster.Remove("Deckhand"); else roster["Deckhand"] = deckhands - 6;
        roster["Marine"] = 6;
        NativeType("Source.Personnel.CrewData").GetMethod("NotifyCrewChanged", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        WriteAtomic("dungeon-reward-fixture.txt", new[] { "Copied-save fixture only", "Six Deckhands replaced by six Marines before accounting baseline; total crew preserved. Not API provisioning evidence." });
    }
    private sealed class DungeonRewardProbe : IDisposable
    {
        private readonly IDungeonRewardProvider _provider;
        private readonly IDisposable _policy;
        private readonly object _player, _donor, _cargo, _item;
        private readonly MethodInfo _count;
        private readonly int _before;
        internal int LootCalls;
        internal DungeonRewardProbe()
        {
            var itemType = NativeType("Behaviour.Item.InventoryItemType");
            object?[] args = { "Titanium Plate", null };
            Require((bool)itemType.GetMethod("TryGet", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args)! && args[1] != null, "Reward item missing.");
            _item = args[1]!;
            _player = SpGet(NativeType("Source.Player.GamePlayer"), "current")!;
            _donor = SpGet(_player, "currentSpaceShip")!;
            _cargo = SpGet(_donor, "cargo")!;
            _count = _cargo.GetType().GetMethod("GetCount", new[] { itemType })!;
            _before = Convert.ToInt32(_count.Invoke(_cargo, new[] { _item }));
            _provider = ModApi.Services.DungeonRewards.AcquireProvider(Id + ".reward");
            try { _policy = _provider.Register("double-loot", DungeonRewardKind.LootAmount, context => { LootCalls++; return new DungeonRewardAdjustment(2); }); }
            catch { _provider.Dispose(); throw; }
        }
        internal (int Before, int After) Verify()
        {
            Require(ReferenceEquals(_player, SpGet(NativeType("Source.Player.GamePlayer"), "current")) && ReferenceEquals(_donor, SpGet(_player, "currentSpaceShip")) && ReferenceEquals(_cargo, SpGet(_donor, "cargo")), "Reward recipient changed.");
            var after = Convert.ToInt32(_count.Invoke(_cargo, new[] { _item }));
            Require(LootCalls > 0 && after - _before == 4, "Reward delivery did not match two authored items with multiplier two.");
            return (_before, after);
        }
        public void Dispose() { _policy.Dispose(); _provider.Dispose(); }
    }
}
