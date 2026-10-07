# Native creature model initialization

Creature creation and respawn resolve the selected display before `Map.AddObject`. The world
feature supplies optional build-5875 `CreatureDisplayInfo.dbc` and `CreatureModelData.dbc`
metadata through a narrow display lookup; the game assembly does not depend on the world feature.

The native scale is the DBC display scale multiplied by model scale. A vmangos template display
slot may provide an object-scale override; zero falls back to the DBC native scale. For cmangos
data, the single `creature_template.scale` remains the selected-display override. Bounding radius,
combat reach, and collision height use `objectScale / nativeScale`, with the existing
geometry fallbacks when model data is unavailable.

The initializer runs before a creature's first map add and again before respawn visibility. It
stores native scale separately from active form/transform state so later visual auras can restore
the native snapshot. Player initialization runs before map insertion; a socket test checks the
native scale and geometry in the first create packet. World schema 23 persists all four template
display-scale overrides. Original-client acceptance and a socket creature respawn assertion remain
follow-up work. `PlayerLoggedIn` is too late to repair a self-create block.
