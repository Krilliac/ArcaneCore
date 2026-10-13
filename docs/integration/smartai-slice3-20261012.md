# SmartAI slice 3a: creature emote, evade, aura and despawn actions (2026-10-12)

Grok lane, branch `grok/w37-smartai-slice3`. Builds on slice 2 (`smartai-slice2-20261010.md`). Numbering is AzerothCore's.

## Added actions (creature and timed action list sources)

| Action | Name | Behaviour here |
|---|---|---|
| 5 | PLAY_EMOTE | Every creature target plays param1 through `CreatureMapSystem.PlayEmote` (SMSG_EMOTE for a one-shot emote). |
| 17 | SET_EMOTE_STATE | Every unit target gets `UNIT_NPC_EMOTESTATE` = param1. |
| 24 | EVADE | The script's own creature evades (`CreatureMapSystem.EnterEvadeMode`); the target is ignored, as in AzerothCore. |
| 28 | REMOVEAURASFROMSPELL | Every unit target loses the auras of spell param1; param1 0 strips every aura (creature targets only). |
| 41 | FORCE_DESPAWN | Every creature target despawns after param1 ms (0 at once) through `CreatureMapSystem.ForcedDespawn`. |

The creature action set is now `1, 5, 11, 12, 17, 22, 23, 24, 28, 30, 31, 41, 67, 69, 73, 74, 80, 87, 88`. Game object and area trigger sets
are unchanged; a game object row with action 24 is refused with "a game object cannot evade". A timed action list run by a game object skips
action 24 (it needs a creature owner, like 12 and 69).

## Deviations

- **PLAY_EMOTE on players.** AzerothCore plays the emote on any unit target. There is no player emote seam on this path, so player targets
  are skipped. `PlayEmote` also keeps a state emote (EMOTE_STATE_*) as the emote state rather than playing it once, as the DB-script path does.
- **REMOVEAURASFROMSPELL 0 on players.** "Remove all auras" exists only for creatures here (`CreatureMapSystem.RemoveAllAuras`), so a player
  target with param1 0 keeps its auras. The `onlyOwnedAuras` param is ignored.
- **FORCE_DESPAWN respawn param.** AzerothCore's param2 (forced respawn timer) is ignored; a database spawn respawns after its own delay.

## Still open (slice 3b and later)

The remaining rejected events, actions and targets in the slice-2 tables, AIName for game objects, and ending the area-trigger handler when a
script ran.
