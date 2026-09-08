using System;
using System.Collections.Generic;
using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class BarNativeSerializationTests
{
    public readonly struct Value
    {
        public readonly object? Data;
        public Value(string? value) { Data = value; }
        public Value(JsonObject value) { Data = value; }
        public Value(JsonArray value) { Data = value; }
    }
    public sealed class JsonObject
    {
        public Dictionary<string, Value> Fields = new();
        public void Add(string key, Value value) => Fields.Add(key, value);
    }
    public sealed class JsonArray
    {
        public List<Value> Items = new();
        public void Add(Value value) => Items.Add(value);
    }
    public sealed class Station { public string guid = "station"; public Bar bar = new(); }
    public sealed class Player { public static Player? current; public object? currentPointOfInterest; }
    public class Patron
    {
        private bool initialized = false;
        public bool Initialized => initialized;
        public Action? DuringSerialization;
        public int Calls;
        public int seat = 1;
        public Value ToJson() { Calls++; DuringSerialization?.Invoke(); return new Value("native"); }
    }
    public sealed class Salesman : Patron
    {
        public string _name = "", description = "";
        public bool _isMale = false;
        public UnityEngine.Sprite? _icon;
        public Salesman(string seed, Station station) { }
    }
    public sealed class Bar
    {
        public List<Patron> availablePatrons = new();
        private long lastUpdateTime = 123;
        private string nextUpdateSeed = "next";
        public string Metadata => lastUpdateTime + nextUpdateSeed;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnedContactsAreExcludedWithoutMutatingTheRoster(bool mutateDuringNativeSerialization)
    {
        var factory = new BarNativeContacts(typeof(Salesman), typeof(Patron), typeof(Station), _ => new UnityEngine.Sprite());
        var serializer = new BarNativeSerialization(typeof(Bar), typeof(Patron), typeof(Value), typeof(JsonObject), typeof(JsonArray), factory);
        var contact = (Salesman)factory.Create(new BarPatronState(new BarPatronId("author", "contact"), "station", "Name", "Description", "seed"), new Station())!;
        var vanilla = new Patron(); var bar = new Bar();
        bar.availablePatrons.AddRange(new Patron[] { vanilla, contact });
        var original = bar.availablePatrons;
        vanilla.DuringSerialization = () =>
        {
            Assert.Same(original, bar.availablePatrons);
            Assert.Contains(contact, bar.availablePatrons);
            if (mutateDuringNativeSerialization) bar.availablePatrons = new List<Patron>();
        };
        if (mutateDuringNativeSerialization)
            Assert.Throws<InvalidOperationException>(() => serializer.TrySerialize(bar, () => true, out _));
        else
        {
            Assert.True(serializer.TrySerialize(bar, () => true, out var result));
            var obj = (JsonObject)((Value)result!).Data!;
            Assert.Single(((JsonArray)obj.Fields["availablePatrons"].Data!).Items);
            Assert.Equal("123", obj.Fields["lastUpdateTime"].Data);
            Assert.Equal("next", obj.Fields["nextUpdateSeed"].Data);
            Assert.Same(original, bar.availablePatrons);
        }
        Assert.Equal(0, contact.Calls);
        Assert.Equal(1, vanilla.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ExclusiveRosterPreservesSuppressedVanillaOrRefusesUncertainSerialization(int mutation)
    {
        var factory = new BarNativeContacts(typeof(Salesman), typeof(Patron), typeof(Station), _ => new UnityEngine.Sprite());
        var station = new Station(); var vanilla = new Patron(); station.bar.availablePatrons.Add(vanilla);
        Player.current = new Player { currentPointOfInterest = station };
        var world = new BarNativeWorld(typeof(Station), typeof(Bar), typeof(Patron), new BarStationSource(typeof(Player), typeof(Station)), factory.IsOwned, factory.Create, 5);
        var state = new BarPatronState(new BarPatronId("author", "contact"), "station", "Name", "Description", "seed");
        var contact = world.CreateContact(state)!;
        Assert.True(world.Apply(world.Capture("station")!, new[] { contact }, () => true));
        var serializer = new BarNativeSerialization(typeof(Bar), typeof(Patron), typeof(Value), typeof(JsonObject), typeof(JsonArray), factory, world);
        if (mutation >= 3)
        {
            vanilla.DuringSerialization = () =>
            {
                var refresh = world.BeginNativeRefresh(station.bar);
                if (mutation == 4) Assert.False(world.CompleteNativeRefresh(refresh, true, true));
            };
            Assert.Throws<InvalidOperationException>(() => serializer.TrySerialize(station.bar, () => true, out _));
            Assert.Equal(1, vanilla.Calls);
            return;
        }
        if (mutation >= 0)
        {
            if (mutation == 0) station.bar.availablePatrons.Add(new Patron());
            else if (mutation == 1) station.bar.availablePatrons.Clear();
            else station.bar.availablePatrons = new List<Patron>(station.bar.availablePatrons);
            Assert.Throws<InvalidOperationException>(() => serializer.TrySerialize(station.bar, () => true, out _));
            Assert.Equal(0, vanilla.Calls);
            return;
        }
        Assert.True(serializer.TrySerialize(station.bar, () => true, out var result));
        var obj = (JsonObject)((Value)result!).Data!;
        Assert.Single(((JsonArray)obj.Fields["availablePatrons"].Data!).Items);
        Assert.Equal(1, vanilla.Calls);
        Assert.Same(contact, Assert.Single(station.bar.availablePatrons));
    }

    [Fact]
    public void VanillaOnlyRosterUsesOriginalSerializer()
    {
        var factory = new BarNativeContacts(typeof(Salesman), typeof(Patron), typeof(Station), _ => null);
        var serializer = new BarNativeSerialization(typeof(Bar), typeof(Patron), typeof(Value), typeof(JsonObject), typeof(JsonArray), factory);
        Assert.False(serializer.TrySerialize(new Bar(), () => false, out _));
    }
}
