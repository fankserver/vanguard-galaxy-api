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
    bool MutationAllowed { get; }
    string Status { get; }
}

/// <summary>Optional since 0.1.2. Register before loading a session; no implicit legacy data adoption.</summary>
public interface IPersistenceApi
{
    IPersistenceRegistration Register(PersistenceProvider provider);
}
