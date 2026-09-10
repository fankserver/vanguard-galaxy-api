using System;

namespace VGModAPI.Tests;

internal abstract class TestSaveDataService : FakeServiceStatus, ISaveDataService, ISaveDataRegistration
{
    private readonly Guid _session = Guid.NewGuid();
    public abstract bool CanRead { get; }
    public abstract bool CanMutate { get; }
    public virtual SaveDataState State => CanRead ? new SaveDataState(SaveDataStateKind.Ready, _session) :
        new SaveDataState(SaveDataStateKind.Blocked, _session, SaveDataBlockReason.LoadRefused);
    public event Action<SaveDataState>? StateChanged { add { } remove { } }
    public abstract SaveDataRegistrationResult Register(PersistenceProvider provider);
    public abstract void Dispose();
}
