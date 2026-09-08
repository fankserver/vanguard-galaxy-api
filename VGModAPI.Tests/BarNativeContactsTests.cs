using VGModAPI;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace UnityEngine { public sealed class Sprite { } }

namespace VGModAPI.Tests
{
    public sealed class BarNativeContactsTests
    {
        public sealed class Station { }
        public class Patron
        {
            private bool initialized = false;
            public int InitializationCalls;
            public void Initialize() { if (!initialized) { InitializationCalls++; initialized = true; } }
        }
        public sealed class Salesman : Patron
        {
            public string _name = "";
            public string description = "";
            public bool _isMale = false;
            public UnityEngine.Sprite? _icon;
            public string Seed;
            public Station Station;
            public Salesman(string seed, Station station) { Seed = seed; Station = station; }
        }

        [Fact]
        public void ContactsAreOwnedInertPresentationsRatherThanRandomNativeSales()
        {
            var icon = new UnityEngine.Sprite();
            var factory = new BarNativeContacts(typeof(Salesman), typeof(Patron), typeof(Station), _ => icon);
            var state = new BarPatronState(new BarPatronId("author", "contact"), "station", "Élodie", "Description", "seed");
            var station = new Station();
            var contact = (Salesman)factory.Create(state, station)!;
            contact.Initialize();
            Assert.Equal(0, contact.InitializationCalls);
            Assert.Equal(state.Name, contact._name);
            Assert.Equal(state.Description, contact.description);
            Assert.Equal(state.Seed, contact.Seed);
            Assert.Same(station, contact.Station);
            Assert.Same(icon, contact._icon);
            Assert.True(factory.TryGet(contact, out var retained));
            Assert.Same(state, retained);
            Assert.False(factory.IsOwned(new Salesman("native", station)));
        }

        [Fact]
        public void MissingPortraitRefusesConstruction()
        {
            var factory = new BarNativeContacts(typeof(Salesman), typeof(Patron), typeof(Station), _ => null);
            Assert.Null(factory.Create(new BarPatronState(new BarPatronId("author", "contact"), "station", "Name", "Description", "seed"), new Station()));
        }
    }
}
