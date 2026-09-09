using System;
using System.Collections.Generic;
using System.IO;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests;

public sealed class WorldOwnedPoiReaderTests
{
    public sealed class Value
    {
        internal object? Data;
        public double AsNumber => (double)Data!;
        public static implicit operator string?(Value value) => value.Data as string;
    }
    public sealed class Json
    {
        public Value this[string key] => new() { Data = key == "lastVisitedTime" ? 3.0 : key == "lastVisitedX" ? 4.0 : "retained" };
    }
    public class Poi
    {
        public string? dangerLevel, hazardsDescription;
        public float lastVisitedTime, lastVisitedX;
        public bool storeLastX => true;
        internal static readonly List<string> Calls = new();
        internal static Action? Loading;
        public void LoadFromJson(Json json) { Calls.Add("load"); Loading?.Invoke(); }
        private static void LoadOptionalPoiData(Poi poi, Json json)
        {
            Assert.Equal(3f, poi.lastVisitedTime); Assert.Equal(4f, poi.lastVisitedX);
            Assert.Equal("retained", poi.dangerLevel); Calls.Add("optional");
        }
    }
    public sealed class Combat : Poi { public Combat() { Calls.Add("construct"); } }
    [Fact]
    public void AuthenticatedJsonReaderPreservesNativeOrderingAndStopsBeforeConstructionOnRefusal()
    {
        var reader = new WorldOwnedPoiReader(typeof(Combat), typeof(Poi), typeof(Json), typeof(Value));
        Poi.Calls.Clear(); Poi.Loading = null;
        Assert.Throws<InvalidDataException>(() => reader.Read(new Json(), () => throw new InvalidDataException("refused")));
        Assert.Empty(Poi.Calls);
        Assert.IsType<Combat>(reader.Read(new Json(), () => { }));
        Assert.Equal(new[] { "construct", "load", "optional" }, Poi.Calls);
    }
    [Fact]
    public void AdmissionLossAfterLoadStopsOptionalFactoriesAndPreservesExceptions()
    {
        var reader = new WorldOwnedPoiReader(typeof(Combat), typeof(Poi), typeof(Json), typeof(Value));
        Poi.Calls.Clear(); bool admitted = true; Poi.Loading = () => admitted = false;
        try
        {
            Assert.Throws<InvalidDataException>(() => reader.Read(new Json(), () => { if (!admitted) throw new InvalidDataException(); }));
            Assert.Equal(new[] { "construct", "load" }, Poi.Calls);
            var failure = new InvalidOperationException("native load"); Poi.Loading = () => throw failure;
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => reader.Read(new Json(), () => { })));
        }
        finally { Poi.Loading = null; }
    }
}
