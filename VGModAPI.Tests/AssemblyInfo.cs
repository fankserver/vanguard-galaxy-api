using Xunit;

// Several adapter fixtures exercise the same runtime globals (GamePlayer.current,
// Singleton<TravelManager>.instance, SpaceStationInterior.instance) that reflection
// bindings read directly. Serializing class execution prevents cross-class static races
// while keeping each test hermetic.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
