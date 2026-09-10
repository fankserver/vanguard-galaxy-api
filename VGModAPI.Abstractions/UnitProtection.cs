using System;

namespace VGModAPI;

/// <summary>
/// Keeps exactly identified story-critical units alive while a declaration is held. Damage still
/// plays out normally — impacts, shield flashes and reactions — but the unit's recorded condition
/// is restored afterwards, so it can never be destroyed and never accumulates lasting harm.
/// Protection is runtime-only: nothing is written to unit data or saves, and disposing the
/// declaration (or removing the mod) restores completely vanilla behavior.
/// </summary>
public interface IUnitProtectionService : IServiceStatus
{
    /// <summary>
    /// Protects the one unit whose persistent unit-data identity matches; no live instance is
    /// required, so a persisted unit can be protected before it materialises. Identically named or
    /// same-class units, including the player's ship, are unaffected. The declaration reasserts
    /// itself whenever that unit is damaged, including after save/load and re-materialisation.
    /// Declare once and retain the result: each call creates an independent declaration that lasts
    /// until disposed. Becoming a boardable wreck is governed separately by boarding rules.
    /// </summary>
    IDisposable Protect(string unitId);
}
