using System;

namespace VGModAPI;

/// <summary>A normalized, linear-independent UI color; components are in [0, 1].</summary>
public readonly struct UiColor : IEquatable<UiColor>
{
    public float Red { get; }
    public float Green { get; }
    public float Blue { get; }
    public float Alpha { get; }
    public UiColor(float red, float green, float blue, float alpha = 1)
    {
        Validate(red); Validate(green); Validate(blue); Validate(alpha);
        Red = red; Green = green; Blue = blue; Alpha = alpha;
    }
    private static void Validate(float value)
    {
        if (float.IsNaN(value) || value < 0 || value > 1)
            throw new ArgumentOutOfRangeException(nameof(value));
    }
    public bool Equals(UiColor other) => Red == other.Red && Green == other.Green && Blue == other.Blue && Alpha == other.Alpha;
    public override bool Equals(object? obj) => obj is UiColor other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Red, Green, Blue, Alpha);
}

/// <summary>The actual item associated with a vanilla floating pickup notification, not a name lookup.</summary>
public sealed class ItemPickupPresentation
{
    public string ItemId { get; }
    public string DisplayName { get; }
    public int Count { get; }
    /// <summary>The game's rarity palette color, or null for standard rarity (leave vanilla untinted).</summary>
    public UiColor? RarityColor { get; }
    internal ItemPickupPresentation(string itemId, string displayName, int count, UiColor? rarityColor)
    { ItemId = itemId; DisplayName = displayName; Count = count; RarityColor = rarityColor; }
}

/// <summary>Style vanilla item pickup notifications without intercepting gameplay or accessing Unity objects.</summary>
public interface IPickupPresentationService : IServiceStatus
{
    /// <summary>Resolve color synchronously on the main thread. Return null to leave presentation to the next
    /// registration or vanilla. The first non-null result wins in registration order. Exceptions are isolated.
    /// Callbacks must only resolve presentation, not mutate gameplay. Dispose to remove the resolver.
    /// Registrations persist across games; credits notifications outside item pickup are excluded.</summary>
    IDisposable Register(string pluginId, Func<ItemPickupPresentation, UiColor?> resolveColor);
}
