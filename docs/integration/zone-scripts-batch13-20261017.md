# Zone scripts, batch 13 (2026-10-17)

Ported from mangos-classic ScriptDev2 kalimdor/darkshore.cpp. Neither script existed in ArcaneCore before this batch: `ScriptedEvents` gated
event 3938 off, but nothing ran the furbolg sequence.

| Script | Source | Where |
| --- | --- | --- |
| `npc_corrupted_furbolg` (Blackwood Warrior 2168, Blackwood Totemic 2169) | darkshore.cpp:656-820 | `Creatures/Scripts/CorruptedFurbolgAI.cs`, `CreatureAiServices` |
| `event_purify_food` (event 3938, quest 4763's bonfire) | darkshore.cpp:822-861 | `KalimdorWorldMapData.OnSpellEvent(caster, eventId, target)` |

## Engine seam

`InstanceData.OnSpellEvent(Unit caster, uint eventId, WorldObject? target)` is a new virtual that carries the event's target, which is
cmangos ProcessEventId's `target`. By default it calls the existing two-argument overload, so no other instance script changes.
`ScriptedEvents.Start` now calls the three-argument form.

## Behaviour

- **In combat.** The warrior takes Battle Stance on reset. It casts Thunderclap 16-21 s into the fight and then every 16-21 s while its
  victim is in melee range. The totemic drops a Healing Ward 6-12 s in and then every 30-36 s. Either one flees once at 15% health, with
  the generic flee emote (`DoFlee`: a 7 s timed flight).
- **Purified food.** The event fires from a player at the bonfire. Two Blackwood Warriors appear at random points within 30 yd of the
  bonfire, with a two-minute out-of-combat timer. Every furbolg within 40 yd of the bonfire, plus the two new warriors, is lured if it is
  alive, out of combat and able to attack the player. The bowl is 5 yd in front of the player.
  - A lured furbolg starts at once if it is the last one drawn; the others start after 0-5 s.
  - It then drops combat, turns passive and immune to players, and runs to the bowl.
  - 5 s after it arrives it turns friendly (faction 35, restored on combat stop) and walks at random within 40 yd of the bowl.
  - 90 s later it evades home, which resets its script.
  - Only the last furbolg drawn emotes, both the "lured" and the "devour" lines.
- **DB script.** The handler returns false, so the event's DB script (Xabraxxis) still runs. With any other source or target the event
  counts as handled and nothing runs, as before.

## Deviations

- The bowl point is 5 yd in front of the player, without `GetFirstCollisionPosition`'s collision check.
- The contact point at the bowl is a straight-line offset, not `GetNearPointAt`.

## Tests

`ZoneScriptBatch13Tests` (Game) covers:
- the entries and Battle Stance;
- Thunderclap and Healing Ward timing;
- the single flee;
- the whole purification run, including the 40 yd cutoff and restoring faction and flags;
- the single emote;
- skipping a furbolg that is already fighting;
- the gate when there is no object target.
