using System;
using System.Collections.Generic;
using VGModAPI.Core;
using Xunit;

namespace VGModAPI.Tests;

public sealed class DialogueServiceTests
{
    private sealed class Status : IServiceStatus
    {
        public ServiceAvailability Availability => ServiceAvailability.Available;
        public event Action<ServiceAvailability>? AvailabilityChanged { add { } remove { } }
    }
    private static DialogueService Create() => new(new Status(), () => { }, _ => { });
    [Fact]
    public void CompetingPresentersCannotStealCurrentLineAndStaleWorkIsCancelled()
    {
        using var service = Create(); var conversation = new object();
        service.Present(conversation, 0, "speaker", "line");
        var first = service.Current!;
        using var owner = service.TryAcquirePresentation("tts", first.ConversationId, first.Sequence)!;
        Assert.NotNull(owner);
        Assert.Null(service.TryAcquirePresentation("other", first.ConversationId, first.Sequence));
        service.Present(conversation, 1, "speaker", "next");
        Assert.True(owner.Cancellation.IsCancellationRequested);
        Assert.False(owner.IsCurrent);
        Assert.Null(service.TryAcquirePresentation("tts", first.ConversationId, first.Sequence));
        Assert.NotNull(service.TryAcquirePresentation("other", service.Current!.ConversationId, service.Current.Sequence));
    }
    [Fact]
    public void ReleasingPresentationDoesNotAllowDuplicateSpeechForTheSameLine()
    {
        using var service = Create(); service.Present(new object(), 0, "A", "line");
        var frame = service.Current!;
        service.TryAcquirePresentation("first", frame.ConversationId, frame.Sequence)!.Dispose();
        Assert.Null(service.TryAcquirePresentation("second", frame.ConversationId, frame.Sequence));
    }
    [Fact]
    public void DuplicateNativePresentationDoesNotDuplicateSpeechAndPreviousLineCanReplay()
    {
        using var service = Create(); var rows = new List<DialogueSnapshot>(); var conversation = new object();
        using var subscription = service.Subscribe(rows.Add);
        service.Present(conversation, 0, "A", "one"); service.Present(conversation, 0, "A", "one");
        service.Present(conversation, 1, "B", "two"); service.Present(conversation, 0, "A", "one");
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows[0].ConversationId, rows[2].ConversationId);
        Assert.NotEqual(rows[0].Sequence, rows[2].Sequence);
    }
    [Fact]
    public void SessionResetCancelsWorkAndRefusesReentrantClaims()
    {
        using var service = Create(); var old = Guid.NewGuid(); service.Reset(old);
        service.Present(new object(), 0, "A", "one"); var frame = service.Current!;
        using var owner = service.TryAcquirePresentation("tts", frame.ConversationId, frame.Sequence)!;
        using var callback = owner.Cancellation.Register(() => service.Present(new object(), 0, "late", "stale"));
        service.Reset(Guid.NewGuid());
        Assert.Null(service.Current); Assert.True(owner.Cancellation.IsCancellationRequested);
        Assert.Null(service.TryAcquirePresentation("tts", frame.ConversationId, frame.Sequence));
    }
    [Fact]
    public void CancellationReentrancyCannotPublishAnObsoleteReplacement()
    {
        using var service = Create(); var events = new List<string>();
        using var subscription = service.Subscribe(x => events.Add(x.Text));
        var conversation = new object(); service.Present(conversation, 0, "A", "first");
        using var owner = service.TryAcquirePresentation("tts", service.Current!.ConversationId, service.Current.Sequence)!;
        using var callback = owner.Cancellation.Register(() => service.Present(conversation, 2, "A", "latest"));
        service.Present(conversation, 1, "A", "superseded");
        Assert.Equal(new[] { "first", "latest" }, events);
        service.Close(); Assert.Null(service.Current);
    }
}
