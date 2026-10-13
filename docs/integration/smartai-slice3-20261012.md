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

## Slice 3b (branch `grok/w37-smartai-slice3b`)

| Action | Name | Behaviour here |
|---|---|---|
| 2 | SET_FACTION | Every creature target takes faction param1; 0 restores its template faction. |
| 3 | MORPH_TO_ENTRY_OR_MODEL | Every creature target wears a model of template param1, else model param2, else its native model. |
| 20 | AUTO_ATTACK | `me` starts (param1 1) or stops (0) its melee swing. |
| 21 | ALLOW_COMBAT_MOVEMENT | `me` chases its victim (1) or stands (0); no change when already so. |
| 37 | DIE | `me` dies without a killer (`CreatureMapSystem.KillCreature`). |

Actions 20, 21 and 37 act on the script's own creature, so a game object row with them is refused, and a game-object-run timed list skips them.
The creature action set is now `1, 2, 3, 5, 11, 12, 17, 20, 21, 22, 23, 24, 28, 30, 31, 37, 41, 67, 69, 73, 74, 80, 87, 88`.

## Still open (slice 3c and later)

The remaining rejected events, actions and targets in the slice-2 tables, AIName for game objects, and ending the area-trigger handler when a
script ran.
