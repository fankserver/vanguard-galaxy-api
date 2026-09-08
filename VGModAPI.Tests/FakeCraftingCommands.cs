using System.Collections.Generic;

namespace Source.Player;

public sealed partial class GamePlayer
{
    public Dictionary<string, bool> RegisterFlags { get; } = new();
}

public static class Register
{
    public static bool HasFlag(string name, bool defaultValue = false) => GamePlayer.current != null && GamePlayer.current.RegisterFlags.TryGetValue(name, out var value) ? value : defaultValue;
    public static void SetFlag(string name, bool value = true) => GamePlayer.current!.RegisterFlags[name] = value;
}
