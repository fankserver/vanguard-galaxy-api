using System;
using HarmonyLib;

namespace VGModAPI.Qualification;

public sealed partial class Plugin
{
    // Queue admission precedes the temporary capacity change: delivery must re-evaluate routing.
    private void CompleteForgeWithFullCargo(object job, float duration)
    {
        var cargo = SpGet(SpGet(CurrentPlayer, "currentSpaceShip")!, "cargo")!;
        var capacity = AccessTools.Property(cargo.GetType(), "capacity");
        var original = (float)capacity.GetValue(cargo);
        try
        {
            capacity.SetValue(cargo, 0f);
            Require((bool)SpCall(cargo, "IsFull", 1f), "Cargo fixture must report full through native capacity logic.");
            SpCall(job, "ProgressJob", duration);
        }
        finally { capacity.SetValue(cargo, original); }
        Require((float)capacity.GetValue(cargo) == original, "Cargo capacity was not restored.");
    }
}
