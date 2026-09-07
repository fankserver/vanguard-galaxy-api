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
    /// a save already in flight. Use <see cref="StateReady"/> to decide whether restored state can be
    /// READ; a read is safe in those transient moments, a mutation is not.
    /// </summary>
    bool MutationAllowed { get; }
    /// <summary>
    /// Whether this owner's state for the CURRENT session was restored and is readable. It stays true
    /// while callbacks dispatch and while a save is in flight, and false when there is no current
    /// session, when the owner's data was blocked, unreadable or failed to restore, or when
    /// publication is blocked.
    /// </summary>
    bool StateReady { get; }
    string Status { get; }
}

/// <summary>Optional since 0.1.2. Register before loading a session; no implicit legacy data adoption.</summary>
public interface IPersistenceApi
{
    IPersistenceRegistration Register(PersistenceProvider provider);
}
