using System;
using System.Collections.Generic;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarPortraitTests
{
    private static BarPatronState State(CharacterPortrait? portrait = null) => new(new BarPatronId("author", "contact"),
        "station", "Captain", "Contact", "seat-seed", portrait: portrait);

    [Fact]
    public void NamedAndBorrowedPortraitsRoundTripWithoutChangingTheSeatSeed()
    {
        foreach (var portrait in new[] { CharacterPortrait.Named("M2Captain"), CharacterPortrait.OfCharacter("LuminateCommander") })
        {
            var bytes = BarPatronCodec.Encode(new[] { State(portrait) });
            var restored = Assert.Single(BarPatronCodec.Decode(bytes));
            Assert.Equal(portrait.PortraitName, restored.Portrait!.PortraitName);
            Assert.Equal(portrait.RegistryName, restored.Portrait.RegistryName);
            Assert.Equal("seat-seed", restored.Seed);
            Assert.Equal(bytes, BarPatronCodec.Encode(new[] { restored }));
        }
    }

    [Fact]
    public void LegacyPatronsKeepTheirDefaultPortraitWithoutAdditionalQuotaBytes()
    {
        var current = BarPatronCodec.Encode(new[] { State() });
        var legacy = (byte[])current.Clone(); legacy[4] = 1;
        Assert.True(BarPatronCodec.Validate(legacy));
        var restored = Assert.Single(BarPatronCodec.Decode(legacy));
        Assert.Null(restored.Portrait);
        Assert.Equal(current.Length, BarPatronCodec.Encode(new[] { restored }).Length);
        Assert.Equal(current, BarPatronCodec.Encode(new[] { restored }));
    }

    [Fact]
    public void UnknownPortraitKindsAndLegacyPortraitFlagsAreRefused()
    {
        var bytes = BarPatronCodec.Encode(new[] { State(CharacterPortrait.Named("M2Captain")) });
        var legacy = (byte[])bytes.Clone(); legacy[4] = 1;
        Assert.False(BarPatronCodec.Validate(legacy));
        bytes[bytes.Length - "M2Captain".Length - 3] = 9;
        Assert.False(BarPatronCodec.Validate(bytes));
    }

    [Fact]
    public void NamedPortraitsAreResolvedAgainForRebuiltContacts()
    {
        object current = new UnityEngine.Sprite(); var calls = new List<string>();
        var resolver = new BarPortraitResolver(name => { calls.Add(name); return current; }, _ => throw new Exception("Wrong source"),
            value => value is UnityEngine.Sprite, error => throw error);
        var factory = new BarNativeContacts(typeof(BarNativeContactsTests.Salesman), typeof(BarNativeContactsTests.Patron),
            typeof(BarNativeContactsTests.Station), state => resolver.Resolve(state.Portrait!));
        var restored = Assert.Single(BarPatronCodec.Decode(BarPatronCodec.Encode(new[] { State(CharacterPortrait.Named("M2Captain")) })));
        var first = Assert.IsType<BarNativeContactsTests.Salesman>(factory.Create(restored, new BarNativeContactsTests.Station()));
        Assert.Same(current, first._icon);
        current = new UnityEngine.Sprite();
        var second = Assert.IsType<BarNativeContactsTests.Salesman>(factory.Create(restored, new BarNativeContactsTests.Station()));
        Assert.Same(current, second._icon); Assert.NotSame(first._icon, second._icon);
        Assert.Equal(new[] { "M2Captain", "M2Captain" }, calls);
    }

    [Fact]
    public void MissingNamedArtReportsOnceButStillCreatesAnInertContactAndCanRecover()
    {
        var reports = new List<Exception>(); object? art = null;
        var resolver = new BarPortraitResolver(_ => art, _ => null, value => value is UnityEngine.Sprite, reports.Add);
        var factory = new BarNativeContacts(typeof(BarNativeContactsTests.Salesman), typeof(BarNativeContactsTests.Patron),
            typeof(BarNativeContactsTests.Station), state => resolver.Resolve(state.Portrait!));
        var state = State(CharacterPortrait.Named("MissingArt"));
        var first = Assert.IsType<BarNativeContactsTests.Salesman>(factory.Create(state, new BarNativeContactsTests.Station()));
        first.Initialize(); Assert.Equal(0, first.InitializationCalls); Assert.Null(first._icon); Assert.True(factory.IsOwned(first));
        factory.Create(state, new BarNativeContactsTests.Station()); Assert.Single(reports);
        art = new UnityEngine.Sprite();
        var later = Assert.IsType<BarNativeContactsTests.Salesman>(factory.Create(state, new BarNativeContactsTests.Station()));
        Assert.Same(art, later._icon); Assert.Single(reports);
    }

    [Fact]
    public void BorrowedPortraitUsesTheCharacterRegistryAndRejectsWrongSpriteTypes()
    {
        var calls = new List<string>(); var reports = new List<Exception>();
        var resolver = new BarPortraitResolver(_ => throw new Exception("Wrong source"), name => { calls.Add(name); return "not a sprite"; },
            value => value is UnityEngine.Sprite, reports.Add);
        Assert.Null(resolver.Resolve(CharacterPortrait.OfCharacter("LuminateCommander")));
        Assert.Equal(new[] { "LuminateCommander" }, calls); Assert.Single(reports);
    }
}
