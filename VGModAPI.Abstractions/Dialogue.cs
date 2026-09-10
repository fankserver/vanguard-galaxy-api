using System;
using System.Threading;

namespace VGModAPI;

public enum DialogueChange { LinePresented, Closed }

/// <summary>Immutable conversation-manager observation. Text is the resolved native line, before typewriter completion.</summary>
public sealed class DialogueSnapshot
{
    public DialogueChange Change { get; }
    public Guid? SessionId { get; }
    public Guid ConversationId { get; }
    public long Sequence { get; }
    public string Speaker { get; }
    public string Text { get; }
    public bool IsOpen => Change == DialogueChange.LinePresented;
    public DialogueSnapshot(DialogueChange change, Guid? sessionId, Guid conversationId, long sequence, string speaker, string text)
    { Change = change; SessionId = sessionId; ConversationId = conversationId; Sequence = sequence; Speaker = speaker; Text = text; }
}

/// <summary>Cooperative ownership of presentation such as speech for one observed line, not control of native dialogue.</summary>
public interface IDialoguePresentation : IDisposable
{
    string OwnerId { get; }
    bool IsCurrent { get; }
    /// <summary>Cancelled on line replacement, closure, scene/session change or disposal. Safe to use in async work.</summary>
    CancellationToken Cancellation { get; }
}

public interface IDialogueService : IServiceStatus
{
    /// <summary>Main-thread-only snapshot; null when there is no current conversation.</summary>
    DialogueSnapshot? Current { get; }
    IDisposable Subscribe(Action<DialogueSnapshot> observer);
    /// <summary>First claimant owns this line's optional presentation. Does not stop or replace vanilla dialogue.</summary>
    IDialoguePresentation? TryAcquirePresentation(string ownerId, Guid conversationId, long sequence);
    /// <summary>Introduced and extended named story characters.</summary>
    IStoryCharacterService Characters { get; }
}
