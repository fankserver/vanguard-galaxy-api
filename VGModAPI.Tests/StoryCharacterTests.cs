using System;
using System.Collections.Generic;
using System.Linq;
using Source.Dialogues;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class StoryCharacterTests : IDisposable
{
    private readonly List<Exception> _errors = new();
    private readonly LifecycleHub _hub = new((_, _) => { });
    private readonly StoryCharacterService _service;
    private readonly StoryCharacterRuntime _runtime;

    public StoryCharacterTests()
    {
        _service = new StoryCharacterService(_hub); _service.SetAvailable(true);
        _runtime = new StoryCharacterRuntime(typeof(Character).Assembly, _service, _errors.Add);
        Characters.captain = new Character("Reyna");
        Characters.shipAi = new Character("ECHO");
        Characters.TestVossPortrait = VossPortrait;
    }
    private static readonly UnityEngine.Sprite VossPortrait = new();

    private static Dialogue? Talk(object character)
    {
        var native = (Character)character;
        return native.createDialogue?.Invoke(native);
    }
    private static string[] Spoken(Dialogue dialogue) =>
        dialogue.dialogues!().Select(line => line.character.name + ": " + line.text).ToArray();

    [Fact]
    public void IntroducedCharacterResolvesConsistentlyWithOwnedIdentityContentAndPortrait()
    {
        var step = 1;
        // Portraits resolve through the registry's factory names, not display names.
        using var ricko = _service.Introduce("mod.a", new StoryCharacterDefinition("ricko", "Ricko", "Luminate Ship Mechanic", portraitOf: "QuestgiverHullBlueprints"),
            () => step switch
            {
                1 => new CharacterConversation(new[]
                    { CharacterLine.Self("Still need the canisters."), CharacterLine.Captain("On it."), CharacterLine.ShipAi("Singing continues.") },
                    completed: () => step = 2),
                _ => new CharacterConversation(new[] { CharacterLine.Self("They're still singing.") })
            }, missionHighlights: new[] { "CustomAct3Quest" });
        Assert.Equal("vgmodapi.character.v1.mod.a.ricko", ricko.LookupName);
        Assert.Null(_runtime.ResolveOwned("Ricko")); // Only the owned lookup name resolves.
        Assert.Null(_runtime.ResolveOwned("QuestgiverHullBlueprints"));
        var built = Assert.IsType<Character>(_runtime.ResolveOwned(ricko.LookupName));
        Assert.Equal("Ricko", built.name); Assert.Equal("Luminate Ship Mechanic", built.description);
        Assert.Same(VossPortrait, built.portretSprite);
        Assert.Equal(new[] { "CustomAct3Quest" }, built.missionIds);
        var first = Talk(built)!;
        Assert.Equal(new[] { "Ricko: Still need the canisters.", "Reyna: On it.", "ECHO: Singing continues." }, Spoken(first));
        first.onComplete!();
        // The same built instance consults the live callback again on the next click.
        Assert.Equal(new[] { "Ricko: They're still singing." }, Spoken(Talk(built)!));
        Assert.Empty(_errors);
    }

    [Fact]
    public void RepeatedLookupsRebuildTheCharacterFreshAsStationsReloadTheirLists()
    {
        using var ricko = _service.Introduce("mod.a", new StoryCharacterDefinition("ricko", "Ricko", "Mechanic"),
            () => new CharacterConversation(new[] { CharacterLine.Self("Hello.") }));
        var first = _runtime.ResolveOwned(ricko.LookupName);
        var second = _runtime.ResolveOwned(ricko.LookupName);
        Assert.NotNull(first); Assert.NotNull(second); Assert.NotSame(first, second);
        Assert.Null(((Character)first!).portretSprite);
        Assert.NotNull(Talk((Character)second!));
    }

    [Fact]
    public void ProvidersAreScopedSoSimilarNamesNeverCollideOrCaptureEachOther()
    {
        using var one = _service.Introduce("mod.a", new StoryCharacterDefinition("ricko", "Ricko", "A's Ricko"),
            () => new CharacterConversation(new[] { CharacterLine.Self("A") }));
        using var two = _service.Introduce("mod.b", new StoryCharacterDefinition("ricko", "Ricko", "B's Ricko"),
            () => new CharacterConversation(new[] { CharacterLine.Self("B") }));
        Assert.NotEqual(one.LookupName, two.LookupName);
        Assert.Equal("A's Ricko", ((Character)_runtime.ResolveOwned(one.LookupName)!).description);
        Assert.Equal("B's Ricko", ((Character)_runtime.ResolveOwned(two.LookupName)!).description);
        Assert.Throws<InvalidOperationException>(() => _service.Introduce("mod.a",
            new StoryCharacterDefinition("ricko", "Other", "Duplicate"), () => null));
        one.Dispose();
        Assert.Null(_runtime.ResolveOwned(one.LookupName));
        Assert.NotNull(_runtime.ResolveOwned(two.LookupName));
        using var replacement = _service.Introduce("mod.a", new StoryCharacterDefinition("ricko", "Ricko", "Replacement"),
            () => null);
        Assert.Equal("Replacement", ((Character)_runtime.ResolveOwned(one.LookupName)!).description);
    }

    [Fact]
    public void ExtendingAGameCharacterConsultsOwnerContentFirstAndFallsBackToItsOwnDialogue()
    {
        var vanillaTalks = 0;
        var commander = new Character("Arle");
        commander.createDialogue = _ => { vanillaTalks++; return new Dialogue { dialogues = () => new List<DialogueLine>() }; };
        CharacterConversation? offered = new(new[] { CharacterLine.Self("The Luminate need you.") });
        using var extension = _service.Extend("mod.a", "LuminateCommander", () => offered, new[] { "CustomLuminateQuest" });
        _runtime.ApplyExtensions("LuminateCommander", commander);
        Assert.Equal(new[] { "CustomLuminateQuest" }, commander.missionIds);
        Assert.Equal(new[] { "Arle: The Luminate need you." }, Spoken(Talk(commander)!));
        Assert.Equal(0, vanillaTalks);
        offered = null; // The owner has nothing to say right now: the character's own dialogue runs.
        Assert.NotNull(Talk(commander));
        Assert.Equal(1, vanillaTalks);
        Assert.Empty(_errors);
    }

    [Fact]
    public void UnrelatedCharactersAndDisposedOrUnavailableContentStayCompletelyVanilla()
    {
        var original = new Character("Greg");
        _runtime.ApplyExtensions("Greg", original);
        Assert.Null(original.createDialogue); Assert.Empty(original.missionIds);
        var extension = _service.Extend("mod.a", "Greg", () => new CharacterConversation(new[] { CharacterLine.Self("Hi.") }));
        var extended = new Character("Greg");
        _runtime.ApplyExtensions("Greg", extended);
        extension.Dispose();
        // Already-built instance consults live declarations per click; nothing remains after disposal.
        Assert.Null(Talk(extended));
        var introduced = _service.Introduce("mod.a", new StoryCharacterDefinition("npc", "N", "D"), () => null);
        _service.SetAvailable(false);
        Assert.Null(_runtime.ResolveOwned(introduced.LookupName));
        var offline = new Character("Greg");
        _runtime.ApplyExtensions("Greg", offline);
        Assert.Null(offline.createDialogue);
        _service.SetAvailable(true);
        Assert.NotNull(_runtime.ResolveOwned(introduced.LookupName));
        introduced.Dispose();
        Assert.Empty(_errors);
    }

    [Fact]
    public void MultipleExtensionsAreConsultedInOrderAndConsumerFaultsFallBackToVanilla()
    {
        var vanillaTalks = 0;
        var character = new Character("Arle") { createDialogue = _ => { vanillaTalks++; return null; } };
        using var faulty = _service.Extend("mod.a", "Arle", () => throw new InvalidOperationException("consumer"));
        using var second = _service.Extend("mod.b", "Arle", () => new CharacterConversation(new[] { CharacterLine.Self("B speaks.") }));
        _runtime.ApplyExtensions("Arle", character);
        // A faulty owner is isolated and reported once; later owners still speak.
        Assert.Equal(new[] { "Arle: B speaks." }, Spoken(Talk(character)!));
        Assert.Equal(0, vanillaTalks); Assert.Single(_errors);
        second.Dispose();
        Assert.Null(Talk(character));
        Assert.Equal(1, vanillaTalks); Assert.Single(_errors);
    }

    [Fact]
    public void SpeakerFallbacksAndUnknownNamesProduceNamedSpeakersInsteadOfFailing()
    {
        Characters.captain = null; Characters.shipAi = null;
        using var npc = _service.Introduce("mod.a", new StoryCharacterDefinition("npc", "N", "D"),
            () => new CharacterConversation(new[]
            {
                CharacterLine.Captain("..."), CharacterLine.ShipAi("..."),
                CharacterLine.By("QuestgiverHullBlueprints", "!"),
                // A display name is not a registry name; it degrades to a plain named speaker.
                CharacterLine.By("Voss", "?"),
            }));
        var spoken = Spoken(Talk(_runtime.ResolveOwned(npc.LookupName)!)!);
        Assert.Equal(new[] { "Captain: ...", "ECHO: ...", "Voss: !", "Voss: ?" }, spoken);
        Assert.Empty(_errors);
    }

    [Fact]
    public void UnknownPortraitRegistryNameFailsOpenToNoPortrait()
    {
        using var npc = _service.Introduce("mod.a", new StoryCharacterDefinition("npc", "N", "D", portraitOf: "Voss"),
            () => null);
        var built = Assert.IsType<Character>(_runtime.ResolveOwned(npc.LookupName));
        Assert.Null(built.portretSprite);
        Assert.Empty(_errors);
    }

    [Fact]
    public void ContractValidationRejectsProgrammingErrors()
    {
        Assert.Throws<ArgumentException>(() => new StoryCharacterDefinition(" ", "N", "D"));
        Assert.Throws<ArgumentException>(() => new StoryCharacterDefinition("id", "N", "D", portraitOf: "vgmodapi.character.v1.x.y")
            is var _ ? _service.Introduce("mod.a", new("id", "N", "D", "vgmodapi.character.v1.x.y"), () => null) : null);
        Assert.Throws<ArgumentException>(() => CharacterLine.Self(" "));
        Assert.Throws<ArgumentException>(() => CharacterLine.By(" ", "text"));
        Assert.Throws<ArgumentException>(() => new CharacterConversation(Array.Empty<CharacterLine>()));
        Assert.Throws<ArgumentNullException>(() => _service.Introduce("mod.a", null!, () => null));
        Assert.Throws<ArgumentNullException>(() => _service.Extend("mod.a", "Greg", null!));
        Assert.Throws<ArgumentException>(() => _service.Extend(" ", "Greg", () => null));
        Assert.IsType<InvalidOperationException>(ServiceNotificationTests.OnWorker(
            () => _service.Extend("mod.a", "Greg", () => null)));
        var registration = _service.Introduce("mod.a", new StoryCharacterDefinition("id", "N", "D"), () => null);
        _service.Dispose();
        Assert.Equal(ServiceUnavailableReason.ApiStopped, _service.Availability.Reason);
        Assert.Throws<ObjectDisposedException>(() => _service.Extend("mod.a", "Greg", () => null));
        Assert.Null(_runtime.ResolveOwned(registration.LookupName));
        registration.Dispose(); _service.Dispose();
    }

    public void Dispose()
    {
        Characters.captain = null; Characters.shipAi = null; Characters.TestVossPortrait = null;
        _service.Dispose(); _hub.Dispose();
    }
}
