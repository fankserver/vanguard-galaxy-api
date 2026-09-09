using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using VGModAPI;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    private IEnumerable<object?> CheckForgeMissingContext()
    {
        var ui = ModApi.Services.ForgeUi;
        var before = ui.Current;
        Require(before != null, "Forge context fixture requires a selected recipe.");
        var missing = new RecipeId("vanilla", "forge/qualification-missing-recipe");
        Require(!ModApi.Services.Recipes.Read().Recipes.Any(r => r.Id.Equals(missing)), "Missing recipe fixture is registered.");
        Require(ui.Open(missing) == ForgeNavigationStatus.RecipeUnavailable, "Missing recipe must be refused.");
        Require(ui.Current?.SelectedRecipe.Equals(before!.SelectedRecipe) == true, "Missing recipe changed selection.");
        var interior = SpGet(NativeType("Behaviour.UI.Spacestation.SpaceStationInterior"), "instance");
        var exterior = SpGet(NativeType("SpacestationExteriorManager"), "Instance");
        Require(TravelStationDriver.Alive(interior) && TravelStationDriver.Alive(exterior), "Native station views required for exit.");
        var facts = new List<StationTransition>();
        Action<StationTransition> observe = facts.Add;
        var station = ModApi.Services.Station;
        station.Transitioned += observe;
        try
        {
            // Invoke the native player's exit path, not a fabricated station event or docking state.
            TravelStationDriver.Bind(interior!.GetType(), "ExitSpacestation", typeof(void)).Invoke(interior, null);
            foreach (var frame in Wait(() => facts.Any(f => f.Kind == StationTransitionKind.Leaving), "Native station Leaving after Forge exit")) yield return frame;
            foreach (var frame in Wait(() => !TravelStationDriver.Alive(SpGet(exterior!, "undockingRoutine")), "Native undock completion")) yield return frame;
            foreach (var frame in Settle()) yield return frame;
            Require(facts.Any(f => f.Kind == StationTransitionKind.Undocking), "Native Undocking fact missing.");
            Require(ui.Current == null && GameObject.Find("Mod API Forge actions") == null, "Forge selection/actions survived station exit.");
            Require(ui.Open(before!.SelectedRecipe) == ForgeNavigationStatus.NotAtStation, "Undocked Forge navigation must refuse missing station context.");
            WriteAtomic("forge-missing-context.txt", new[] { "PASS", "missing-recipe-refused", "native-undock-navigation-refused" });
        }
        finally { station.Transitioned -= observe; }
    }
}
