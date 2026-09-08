using System;
using System.Reflection;
using UnityEngine;

namespace VGModAPI.Runtime;

internal sealed class DungeonRecoveryWorld
{
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly TravelNativeBindings _travel;
    private readonly Type _ship, _pod;
    private readonly PropertyInfo _poi;
    private readonly FieldInfo _initializing, _loot, _capacity;
    private readonly MethodInfo _item;
    internal DungeonRecoveryWorld(Assembly assembly, IBoardingTacticalNativeBindings native)
    {
        _native = native; _travel = new(assembly); _ship = assembly.GetType("Behaviour.Unit.SpaceShip", true)!;
        _pod = assembly.GetType("Behaviour.Persistables.BoardingPod", true)!;
        var poi = assembly.GetType("Source.Galaxy.MapPointOfInterest", true)!;
        _poi = poi.GetProperty("current", BindingFlags.Public | BindingFlags.Static)!;
        _initializing = poi.GetField("initializingPersistables", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var loot = assembly.GetType("Behaviour.Managers.LootManager", true)!;
        _loot = assembly.GetType("Behaviour.Util.Singleton`1", true)!.MakeGenericType(loot).GetField("instance", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
        var builder = assembly.GetType("Behaviour.Item.Builder.ItemBuilder", true)!;
        _item = builder.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)!;
        _capacity = builder.GetField("maxCrewPerPod")!;
        if (_poi?.PropertyType != poi || _initializing?.FieldType != typeof(bool) || _loot?.FieldType != loot || _item?.ReturnType != builder || _capacity?.FieldType != typeof(int))
            throw new MissingMemberException("Unexpected return-world binding shape.");
    }
    internal bool Ready(object recipient)
    {
        if (recipient is not Component ship || !ship || !ship.gameObject.activeInHierarchy || _native.Manager is not Component manager || !manager) return false;
        if (_loot.GetValue(null) is not Component loot || !loot) return false;
        var poi = _poi.GetValue(null); if (poi == null || _initializing.GetValue(poi) is true) return false;
        var travel = _travel.TravelManager(); var local = travel == null ? null : _travel.LocalManager(travel);
        if (local == null || !_travel.Ready(local, poi)) return false;
        var item = _item.Invoke(null, new object[] { "CrewPod" });
        return item != null && (int)_capacity.GetValue(item)! > 0;
    }
    internal bool HasLivePod(object data)
    {
        foreach (var pod in UnityEngine.Object.FindObjectsByType(_pod, FindObjectsInactive.Include))
            if (pod && ReferenceEquals(_native.Get(pod, "resumePodData"), data)) return true;
        return false;
    }
    internal object? Resolve(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        object? result = null;
        foreach (var candidate in UnityEngine.Object.FindObjectsByType(_ship))
        {
            if (candidate is not Component component || !component || !component.gameObject.activeInHierarchy) continue;
            if ((string?)_native.Get(_native.Get(candidate, "resumeShipData"), "resumeShipGuid") != id) continue;
            if (result != null) return null;
            result = candidate;
        }
        return result != null && Ready(result) ? result : null;
    }
}
