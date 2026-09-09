namespace VGModAPI.Core;

/// <summary>Pointer input requires its captured left press; unrelated buttons cannot consume it.</summary>
internal sealed class PointerRevision
{
    private long? _pressed;
    internal void Down(bool left, long? revision) { if (left) _pressed = revision; }
    internal long? Click(bool left)
    {
        if (!left) return null;
        var value = _pressed; _pressed = null; return value;
    }
    internal void Clear() => _pressed = null;
}
