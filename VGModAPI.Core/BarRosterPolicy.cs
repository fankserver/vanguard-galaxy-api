using System;
using System.Collections.Generic;
using System.Linq;

namespace VGModAPI.Core;

/// <summary>Station-local ownership decisions, independent of registration or Harmony execution order.</summary>
internal static class BarRosterPolicy
{
    internal sealed class Claim
    {
        internal string Provider { get; }
        internal bool Exclusive { get; }
        internal bool ExclusivePermission { get; }
        internal Claim(string provider, bool exclusive, bool exclusivePermission)
        {
            if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider identity is required.", nameof(provider));
            Provider = provider; Exclusive = exclusive; ExclusivePermission = exclusivePermission;
        }
    }

    internal enum Denial { ExclusivePermissionRequired, ConflictingExclusiveClaims, StationOwnedExclusively }

    internal sealed class Decision
    {
        internal bool KeepVanilla { get; }
        internal IReadOnlyList<string> Admitted { get; }
        internal IReadOnlyDictionary<string, Denial> Denied { get; }
        internal Decision(bool keepVanilla, IEnumerable<string> admitted, IDictionary<string, Denial> denied)
        {
            KeepVanilla = keepVanilla;
            Admitted = Array.AsReadOnly(admitted.OrderBy(value => value, StringComparer.Ordinal).ToArray());
            Denied = new System.Collections.ObjectModel.ReadOnlyDictionary<string, Denial>(new Dictionary<string, Denial>(denied, StringComparer.Ordinal));
        }
    }

    // Callers supply an already bounded snapshot of one claim per provider for one exact station.
    internal static Decision Resolve(IReadOnlyList<Claim> claims)
    {
        if (claims == null) throw new ArgumentNullException(nameof(claims));
        if (claims.Count > 32) throw new ArgumentException("Station provider bound exceeded.", nameof(claims));
        var snapshot = claims.ToArray();
        if (snapshot.Any(claim => claim == null) || snapshot.Select(claim => claim.Provider).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Station claims must have unique providers.", nameof(claims));
        var denied = new Dictionary<string, Denial>(StringComparer.Ordinal);
        foreach (var claim in snapshot.Where(claim => claim.Exclusive && !claim.ExclusivePermission))
            denied.Add(claim.Provider, Denial.ExclusivePermissionRequired);
        var valid = snapshot.Where(claim => !denied.ContainsKey(claim.Provider)).ToArray();
        var exclusive = valid.Where(claim => claim.Exclusive).ToArray();
        if (exclusive.Length > 1)
        {
            foreach (var claim in valid) denied.Add(claim.Provider, Denial.ConflictingExclusiveClaims);
            return new Decision(true, Array.Empty<string>(), denied);
        }
        if (exclusive.Length == 1)
        {
            string owner = exclusive[0].Provider;
            foreach (var claim in valid.Where(claim => claim.Provider != owner)) denied.Add(claim.Provider, Denial.StationOwnedExclusively);
            return new Decision(false, new[] { owner }, denied);
        }
        return new Decision(true, valid.Select(claim => claim.Provider), denied);
    }
}
