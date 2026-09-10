using System;
using System.Collections.Generic;
using System.Threading;

namespace VGModAPI.Core;

internal sealed class DialogueService : IDialogueService, IDisposable
{
    private readonly IServiceStatus _status;
    private readonly Action _checkThread;
    private readonly Action<Exception> _report;
    private readonly List<Observer> _observers = new();
    private DialogueSnapshot? _current;
    private object? _conversation;
    private int _line = -1;
    private long _sequence;
    private Guid? _session;
    private Presentation? _presentation;
    private bool _disposed, _resetting, _claimed;
    public ServiceAvailability Availability => _status.Availability;
    public event Action<ServiceAvailability>? AvailabilityChanged
    { add => _status.AvailabilityChanged += value; remove => _status.AvailabilityChanged -= value; }
    public DialogueSnapshot? Current { get { _checkThread(); return _current; } }
    private readonly StoryCharacterService _characters;
    public IStoryCharacterService Characters { get { _checkThread(); return _characters; } }
    internal DialogueService(IServiceStatus status, Action checkThread, Action<Exception> report, StoryCharacterService characters)
    { _status = status; _checkThread = checkThread; _report = report; _characters = characters ?? throw new ArgumentNullException(nameof(characters)); }

    public IDisposable Subscribe(Action<DialogueSnapshot> observer)
    {
        _checkThread();
        if (_disposed) throw new ObjectDisposedException(nameof(DialogueService));
        if (observer == null) throw new ArgumentNullException(nameof(observer));
        if (_observers.Count >= 128) throw new InvalidOperationException("Dialogue observer limit reached.");
        var item = new Observer(this, observer); _observers.Add(item); return item;
    }
    public IDialoguePresentation? TryAcquirePresentation(string ownerId, Guid conversationId, long sequence)
    {
        _checkThread();
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Presentation owner required.", nameof(ownerId));
        if (_disposed || _resetting || !Availability.IsAvailable || _claimed || _presentation != null || _current == null || !_current.IsOpen ||
            _current.ConversationId != conversationId || _current.Sequence != sequence) return null;
        _claimed = true;
        return _presentation = new Presentation(this, ownerId, _current);
    }
    internal void Present(object conversation, int index, string speaker, string text)
    {
        _checkThread();
        if (_disposed || _resetting || !Availability.IsAvailable || index < 0) return;
        if (ReferenceEquals(conversation, _conversation) && index == _line && _current != null && _current.Speaker == speaker && _current.Text == text) return;
        var id = ReferenceEquals(conversation, _conversation) && _current != null ? _current.ConversationId : Guid.NewGuid();
        _conversation = conversation; _line = index;
        Change(new DialogueSnapshot(DialogueChange.LinePresented, _session, id, ++_sequence, speaker, text));
    }
    internal void Close()
    {
        _checkThread();
        _conversation = null; _line = -1;
        if (_current == null) return;
        Change(new DialogueSnapshot(DialogueChange.Closed, _session, _current.ConversationId, ++_sequence, "", ""));
    }
    internal void Reset(Guid? session)
    {
        _checkThread(); if (_resetting) return;
        _resetting = true;
        try { Close(); _session = session; }
        finally { _resetting = false; }
    }
    private void Change(DialogueSnapshot snapshot)
    {
        _current = snapshot.IsOpen ? snapshot : null;
        _claimed = false;
        var previous = _presentation; _presentation = null;
        previous?.Cancel();
        foreach (var observer in _observers.ToArray())
        {
            if (_disposed || snapshot.Sequence != _sequence) break;
            if (!observer.Active) continue;
            try { observer.Callback(snapshot); } catch (Exception error) { Report(error); }
        }
    }
    private void Report(Exception error) { try { _report(error); } catch { } }
    public void Dispose()
    {
        _checkThread(); if (_disposed) return;
        _disposed = true; Close(); _observers.Clear();
    }
    private sealed class Observer : IDisposable
    {
        private readonly DialogueService _service;
        internal readonly Action<DialogueSnapshot> Callback;
        internal bool Active = true;
        internal Observer(DialogueService service, Action<DialogueSnapshot> callback) { _service = service; Callback = callback; }
        public void Dispose() { _service._checkThread(); Active = false; _service._observers.Remove(this); }
    }
    private sealed class Presentation : IDialoguePresentation
    {
        private readonly DialogueService _service;
        private readonly DialogueSnapshot _snapshot;
        private readonly CancellationTokenSource _cancel = new();
        private bool _ended;
        public string OwnerId { get; }
        public CancellationToken Cancellation { get; }
        internal Presentation(DialogueService service, string owner, DialogueSnapshot snapshot)
        { _service = service; OwnerId = owner; _snapshot = snapshot; Cancellation = _cancel.Token; }
        public bool IsCurrent { get { _service._checkThread(); return !_ended && ReferenceEquals(_service._presentation, this) && ReferenceEquals(_service._current, _snapshot); } }
        internal void Cancel()
        {
            if (_ended) return; _ended = true;
            try { _cancel.Cancel(); } catch (Exception error) { _service.Report(error); }
            finally { _cancel.Dispose(); }
        }
        public void Dispose()
        {
            _service._checkThread();
            if (ReferenceEquals(_service._presentation, this)) _service._presentation = null;
            Cancel();
        }
    }
}
