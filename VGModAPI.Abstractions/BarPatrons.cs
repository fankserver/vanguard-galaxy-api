using System;

namespace VGModAPI;

/// <summary>A patron identity scoped to the authenticated provider, never its displayed name or seat.</summary>
public readonly struct BarPatronId : IEquatable<BarPatronId>
{
    public string Provider { get; }
    public string LocalId { get; }
    public BarPatronId(string provider, string localId)
    {
        // The same restricted segment alphabet keeps all managed-content identities unambiguous.
        var validated = new StoryContentId(provider, localId);
        Provider = validated.Provider; LocalId = validated.LocalId;
    }
    public bool Equals(BarPatronId other) => string.Equals(Provider, other.Provider, StringComparison.Ordinal)
        && string.Equals(LocalId, other.LocalId, StringComparison.Ordinal);
    public override bool Equals(object? value) => value is BarPatronId other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Provider, LocalId);
    public override string ToString() => Provider + "/" + LocalId;
    public static bool operator ==(BarPatronId left, BarPatronId right) => left.Equals(right);
    public static bool operator !=(BarPatronId left, BarPatronId right) => !left.Equals(right);
}

/// <summary>Requested ownership of one exact station's roster; exclusive requests also require host permission.</summary>
public enum BarRosterOwnership
{
    Additive,
    Exclusive
}

/// <summary>Persistent content is API-saved; transient presentation is rebuilt from current registrations.</summary>
public enum BarPatronRetention
{
    Transient,
    Persistent
}
