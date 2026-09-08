using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VGModAPI.Core;

namespace VGModAPI.Runtime;

/// <summary>Builds disabled native pods; activation is the coordinator's final step after return guards are installed.</summary>
internal sealed class DungeonReturnPodFactory
{
    private readonly IBoardingTacticalNativeBindings _native;
    private readonly DungeonReturnCarrier _carriers;
    private readonly Type _data, _phase;
    private readonly FieldInfo _prefab;
    internal DungeonReturnPodFactory(Assembly assembly, IBoardingTacticalNativeBindings native)
    {
        _native = native; _carriers = new(assembly);
        _data = assembly.GetType(DungeonPodResumeBindings.Data, true)!;
        _phase = assembly.GetType("Source.Data.Persistable.BoardingPodState", true)!;
        _prefab = assembly.GetType(BindingCatalog.BoardingManager, true)!.GetField("boardingPodPrefab") ?? throw new MissingFieldException("Pod prefab unavailable.");
        if (_prefab.FieldType.FullName != DungeonPodResumeBindings.Pod || _data.GetConstructor(Type.EmptyTypes) == null) throw new InvalidOperationException("Unexpected pod construction schema.");
    }
    internal (GameObject Object, object Pod, object Data, object Operation) Build(DungeonPodResumeState saved, object recipient, string dungeonType, bool autonomous, object? existingData = null)
    {
        var transport = saved.Transport;
        if (!saved.CanRecover || transport == null || string.IsNullOrEmpty(saved.ParentShipId) || recipient is not Component ship || !ship ||
            (string?)_native.Get(_native.Get(recipient, "resumeShipData"), "resumeShipGuid") != saved.ParentShipId) throw new InvalidOperationException("Exact live return recipient and transport state required.");
        var manager = _native.Manager ?? throw new InvalidOperationException("Dungeon manager unavailable.");
        var prefab = _prefab.GetValue(manager) as Component;
        if (!prefab) throw new InvalidOperationException("Return pod prefab unavailable.");
        var root = new GameObject("ModAPI disabled return construction"); root.SetActive(false);
        GameObject? clone = null;
        try
        {
            clone = UnityEngine.Object.Instantiate(prefab!.gameObject, root.transform); clone.SetActive(false);
            var pod = clone.GetComponent(_prefab.FieldType) ?? throw new InvalidOperationException("Cloned pod component unavailable.");
            if (existingData != null && !_data.IsInstanceOfType(existingData)) throw new InvalidOperationException("Invalid restored pod data type.");
            var data = existingData ?? Activator.CreateInstance(_data)!; var p = transport.Pose;
            _native.Set(data, "resumePodId", transport.NativePodId); _native.Set(data, "resumePodPhase", Enum.Parse(_phase, "Returning"));
            _native.Set(data, "resumePodPlayer", saved.PlayerOwned); _native.Set(data, "resumeParentId", saved.ParentShipId);
            _native.Set(data, "resumePodCrew", new Dictionary<string, int>(transport.OutboundCrew, StringComparer.Ordinal));
            _native.Set(data, "resumePosition", new Vector2(p[0], p[1])); _native.Set(data, "resumeAngle", p[2]);
            _native.Set(data, "resumeHullOffset", new Vector2(p[3], p[4])); _native.Set(data, "resumeTargetPosition", new Vector2(p[5], p[6]));
            _native.Set(data, "resumeAttachmentOffset", new Vector2(p[7], p[8]));
            _native.Call("podInit", pod, data, ship.transform, ship.transform);
            clone.transform.position = new Vector3(p[0], p[1], ship.transform.position.z); clone.transform.rotation = Quaternion.Euler(0, 0, p[2]);
            _native.Call("podReturnStart", pod, ship.transform, new Dictionary<string, int>(saved.ReturnCrew, StringComparer.Ordinal));
            // StartReturning reparents the pod; it remains explicitly inactive throughout construction.
            var operation = _carriers.Create(recipient, dungeonType, autonomous); _carriers.Register(operation, pod, data);
            return (clone, pod, data, operation);
        }
        catch { if (clone) UnityEngine.Object.Destroy(clone); throw; }
        finally { UnityEngine.Object.Destroy(root); }
    }
}
