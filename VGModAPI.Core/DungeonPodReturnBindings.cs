namespace VGModAPI.Core;

internal static class DungeonPodReturnBindings
{
    internal static readonly MethodBinding[] Hooks =
    {
        new("returnReceipt", BindingCatalog.BoardingOperation, "HandlePodCrewReturned", false, "System.Void", DungeonPodResumeBindings.Pod),
        new("returnCrewAdded", "Source.SpaceShip.SpaceShipData", "AddCrew", false, "System.Int32", "System.String", "System.Int32", "System.Boolean"),
        new("returnOverflow", "Behaviour.Managers.LootManager", "CreateCrewPod", false, "System.Void", "System.String", "System.Int32", "UnityEngine.Transform", "System.Boolean", "System.Boolean", "System.Boolean"),
        new("returnPersisted", "Source.Galaxy.MapPointOfInterest", "AddPersistable", false, "UnityEngine.GameObject", "Source.Data.Persistable.PersistableData")
    };
}
