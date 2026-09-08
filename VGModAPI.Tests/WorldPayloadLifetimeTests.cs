using System;
using System.IO;
using VGModAPI.Core;
using VGModAPI.Core.Integration;
using Xunit;

namespace VGModAPI.Tests
{
    public sealed class WorldPayloadLifetimeTests
    {
        [Fact]
        public void TriggeredPayloadRetainsParentQuarantineAcrossIdentityStrippingAndTeardown()
        {
            var hub = new LifecycleHub((_, error) => throw error);
            using var host = new WorldLifetimeHookHost(typeof(Source.Galaxy.MapElement).Assembly, hub);
            hub.Begin(SessionOrigin.NewGame, null);
            var owned = new Source.Galaxy.MapPointOfInterest { guid = WorldObjectIdentity.ReservedPrefix + "unknown" };
            var payload = new Source.Galaxy.MapTriggeredPayload(owned);
            var vanilla = new Source.Galaxy.MapTriggeredPayload(new Source.Galaxy.MapPointOfInterest { guid = "vanilla" });
            Assert.Throws<InvalidDataException>(() => host.RequirePayload(payload)); host.RequirePayload(vanilla);
            owned.guid = "stripped"; host.Dispose();
            Assert.Throws<InvalidDataException>(() => host.RequirePayload(payload)); host.RequirePayload(vanilla);
        }
    }
}
namespace Source.Galaxy
{
    public sealed class MapTriggeredPayload
    {
        public readonly MapPointOfInterest parent;
        public MapTriggeredPayload(MapPointOfInterest parent) => this.parent = parent;
    }
}
