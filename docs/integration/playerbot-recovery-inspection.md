# Playerbot recovery inspection

`.playerbot inspect` reports the live player position and combat death state. When a released ghost has an actual corpse object, it reports the corpse identity, map, and coordinates; 3D distance is reported only for a same-map finite calculation and otherwise is `unavailable`. A living player reports `death=Alive` and `corpse=none`.

The inspection is read-only and bounded by the existing nearby-service (16), known-spell (128), trainer (8), and command reply batching limits. It does not invoke release, reclaim, resurrection, movement, or other player actions.

`delay_remaining_s` uses the public `PlayerLife.Capture` corpse timestamp, the ordinary reclaim-delay rule and the world's death clock. A zero delay does not assert that reclamation is otherwise allowed: distance, map, settlement and other ordinary handler gates still apply.
