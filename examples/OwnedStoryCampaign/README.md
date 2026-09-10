# Owned story authors

Build both independent example plugins with `make build-story-authors CONFIGURATION=Release`.
They are not shipped in the API package.

`OwnedStoryCampaign` supplies a hand-authored two-step mission with a `witness`
decision token. `OwnedStoryJob` takes a generated pitch, destination and reward.
Both use local ID `mission-x` in separate authenticated provider assemblies.
Call their `Register` methods from your own interaction/UI after choosing an
existing destination. Use the returned `Definition` with `game.Story.Offer`, then
call `Activate` on the mission. Definition events carry the mission and its game;
scripted progress uses `mission.GetObjective(key).SetProgress(value)`. Neither
author implements persistence, serialization or a load callback.

A travel objective observes the destination's native visit timestamp; it is not a
dwell timer. These examples do not implement an LLM request or a universal mission
DSL. Use disposable saves when manually trying example content; do not deploy
examples into an existing campaign without explicit intent and backups.
