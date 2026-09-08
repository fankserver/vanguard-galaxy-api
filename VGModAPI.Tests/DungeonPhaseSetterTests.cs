using System;
using System.Linq;
using VGModAPI.Core;
using VGModAPI.Runtime;
using Xunit;

namespace VGModAPI.Tests
{
    public sealed class DungeonPhaseSetterTests
    {
        [Fact]
        public void CatalogResolvesPrivatePhaseSetterInsteadOfTreatingPropertyAsField()
        {
            var operation = new Behaviour.Dungeon.DungeonOperation();
            var binding = Assert.Single(DungeonPodResumeBindings.Methods, item => item.Key == "resumeSetPhase");
            var method = new GameBindings(operation.GetType().Assembly).Resolve(new[] { binding })[binding.Key];
            Assert.True(method.IsPrivate); Assert.True(method.IsSpecialName);
            Assert.Null(operation.GetType().GetField("phase"));
            var value = Enum.Parse(method.GetParameters()[0].ParameterType, "Extraction");
            method.Invoke(operation, new[] { value });
            Assert.Equal(Source.CompartmentSystem.MissionPhase.Extraction, operation.phase);
        }
    }
}
namespace Source.CompartmentSystem
{
    public enum MissionPhase { Approach, Active, Extraction, Complete }
}
namespace Behaviour.Dungeon
{
    public sealed class DungeonOperation
    {
        public Source.CompartmentSystem.MissionPhase phase { get; private set; } = Source.CompartmentSystem.MissionPhase.Active;
    }
}
