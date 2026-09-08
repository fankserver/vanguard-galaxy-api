using System;

namespace VGModAPI.Core;

internal static class ModUpdateControls
{
    // Unity clears keyboard selection when interactable becomes false. Apply only final states,
    // never a temporary disabled state followed by re-enabling the same control during a render.
    internal static void Apply(bool selected, bool canCheck, bool canRelease,
        Action<bool> check, Action<bool> automatic, Action<bool> release)
    {
        check(selected && canCheck);
        automatic(selected);
        release(selected && canRelease);
    }
}
