using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace VGModAPI;

/// <summary>Experimental owner callbacks. Main-thread-only; payloads are owned state, not vanilla mutation hooks.</summary>
public sealed class PersistenceProvider
{
    public string Owner { get; }
    public int SchemaVersion { get; }
    public Func<byte[]> Capture { get; }
    public Action<SessionSnapshot, byte[]?> Restore { get; }
    public Func<byte[], bool> Validate { get; }
    public IReadOnlyDictionary<int, Func<byte[], byte[]>> Migrations { get; }

    public PersistenceProvider(string owner, int schemaVersion, Func<byte[]> capture,
        Action<SessionSnapshot, byte[]?> restore, Func<byte[], bool> validate,
        IReadOnlyDictionary<int, Func<byte[], byte[]>>? migrations = null)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        SchemaVersion = schemaVersion;
        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Restore = restore ?? throw new ArgumentNullException(nameof(restore));
        Validate = validate ?? throw new ArgumentNullException(nameof(validate));
        var copy = new Dictionary<int, Func<byte[], byte[]>>();
        if (migrations != null) foreach (var pair in migrations) copy.Add(pair.Key, pair.Value);
        Migrations = new ReadOnlyDictionary<int, Func<byte[], byte[]>>(copy);
    }
}

/// <summary>Disposal disables this owner; active-session removal conservatively pauses API-managed saves for all registered mods.</summary>
public interface IPersistenceRegistration : IDisposable
{
    /// <summary>
    /// Whether this owner's state may be MUTATED right now. It is false whenever a mutation could not
    /// be published faithfully, which includes transient moments: lifecycle callbacks dispatching and
    /// a save already in flight. To decide whether restored state can be READ, cast the handle to
    /// <see cref="IPersistenceReadiness"/>; a read is safe in those transient moments, a mutation is not.
    /// </summary>
    bool MutationAllowed { get; }
    string Status { get; }
}

/// <summary>
/// Optional readability capability of a registration handle; requires API 0.1.12.
/// Cast the handle returned by <see cref="IPersistenceApi.Register"/>; an implementation of
/// <see cref="IPersistenceRegistration"/> need not supply this capability. Treat its absence as
/// "readiness unknown" and refuse reads rather than assuming state is readable.
/// </summary>
public interface IPersistenceReadiness
{
    /// <summary>
    /// Whether this owner's state for the CURRENT session was restored and is readable. It stays true
    /// while callbacks dispatch and while a save is in flight — moments where reading is safe but
    /// <see cref="IPersistenceRegistration.MutationAllowed"/> is deliberately false — and false when
    /// there is no current session, when the owner's data was blocked, unreadable or failed to
    /// restore, or when publication is blocked.
    /// </summary>
    bool StateReady { get; }
}

/// <summary>Optional service requiring API 0.1.2. Register before loading a session; no implicit legacy data adoption.</summary>
public interface IPersistenceApi
{
    IPersistenceRegistration Register(PersistenceProvider provider);
}
