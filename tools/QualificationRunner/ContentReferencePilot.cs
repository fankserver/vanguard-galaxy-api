using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private void CheckContentReferences()
    {
        if (!File.Exists(Path.Combine(_root!, "content-reference.enabled"))) return;
        var directory = Path.Combine(_root!, "content-reference-fixtures");
        Directory.CreateDirectory(directory);
        foreach (PersistentContentKind kind in Enum.GetValues(typeof(PersistentContentKind)))
        {
            var id = "VGModAPI_Missing_" + Guid.NewGuid().ToString("N");
            var file = Path.Combine(directory, kind + ".reference");
            File.WriteAllLines(file, new[] { "qualification.content", id, kind.ToString(), "1.0.0" });
            var original = File.ReadAllText(file); var fields = File.ReadAllLines(file);
            var reference = new PersistentContentReference(fields[0], fields[1], (PersistentContentKind)Enum.Parse(typeof(PersistentContentKind), fields[2]), new Version(fields[3]));
            var declaration = new ContentDeclaration(reference.Owner, reference.LocalId, reference.Kind, ContentPersistenceImpact.ProviderRequired);
            Require(ContentSafety.Assess(reference, declaration, null, false, true, false, false) == ContentRecoveryAction.RequireProvider, "Missing content did not require its provider.");
            string typeName;
            string methodName;
            Type expected;
            object[] args;
            switch (kind)
            {
                case PersistentContentKind.Item:
                    typeName = "Behaviour.Item.InventoryItemType"; methodName = "Get"; expected = typeof(KeyNotFoundException); args = new object[] { id }; break;
                case PersistentContentKind.Mission:
                    typeName = "Source.MissionSystem.StoryMission"; methodName = "Get"; expected = typeof(KeyNotFoundException); args = new object[] { id }; break;
                case PersistentContentKind.Patron:
                    typeName = "Source.Galaxy.POI.Station.BarPatron"; methodName = "Create"; expected = typeof(NullReferenceException); args = new object[] { id, SpStations()[0] }; break;
                case PersistentContentKind.Faction:
                    typeName = "Source.Galaxy.Faction"; methodName = "Get"; expected = typeof(NullReferenceException); args = new object[] { id }; break;
                default:
                    typeName = "Source.Galaxy.MapPointOfInterest"; methodName = "Create"; expected = typeof(NullReferenceException); args = new object[] { id }; break;
            }
            var type = AccessTools.TypeByName(typeName);
            var method = args.Length == 1 ? type.GetMethod(methodName, new[] { typeof(string) }) : type.GetMethod(methodName);
            try { method!.Invoke(null, args); throw new InvalidOperationException("Unknown vanilla content unexpectedly resolved: " + kind); }
            catch (TargetInvocationException error) { Require(error.InnerException?.GetType() == expected, "Unexpected missing-reference failure: " + kind); }
            Require(File.ReadAllText(file) == original, "Saved reference fixture changed during refusal.");
            Passed("saved-content-reference-refusal-" + kind);
        }
        File.WriteAllText(Path.Combine(_root!, "content-reference.txt"), "PASS\nFive serialized reference fixtures exercised at native lookup/factory boundaries. Not a full foreign-content save-load or safe-uninstall guarantee.");
    }
}
