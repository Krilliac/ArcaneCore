# GM command parity slice (2026-10-08)

The [complete exact-name gap list](vmangos-gm-command-gaps-20261008.md) was generated from vmangos `src/game/Chat/Chat.cpp` (`ChatHandler::getCommandTable`) against the generated ArcaneCore command reference on this worktree. Its 654 remaining paths include groups, debug and bot controls. Exact-name absence does not prove that an operation is missing under another name.

## Implemented

| Command | vmangos behavior ported | ArcaneCore limit |
|---|---|---|
| `.additemset` | `CharacterCommands.cpp`, `ChatHandler::HandleAddItemSetCommand`: grant one of each `item_template` piece with the set id, permit partial storage, unbind a self grant | Uses loaded item templates; no grant if they are absent. |
| `.aura` | `UnitCommands.cpp`, `ChatHandler::HandleAuraCommand` / `HandleAuraHelper`: apply a spell's aura to the selected unit | Uses `SpellSystem.AddAura`; an unknown spell shows the syntax, a spell without an aura effect replies `LANG_SPELL_NO_HAVE_AURAS`. The optional duration argument and area aura effects are not supported. |
| `.die` | `UnitCommands.cpp`, `ChatHandler::HandleDieCommand` / `HandleDieHelper`: a player's god mode (invincibility threshold) is cleared, a creature's loot recipient is cleared (`Creature.ClearLootRecipient`), then the unit deals its whole health to itself: no kill credit, no loot, no durability loss | The vmangos `DieCommandCredit` opt-in is not exposed; a creature's own death prevention still holds, as in vmangos. |
| `.damage` | `UnitCommands.cpp`, `ChatHandler::HandleDamageCommand`: direct, unmitigated physical damage and the GM attack update packet | School-specific mitigation argument is not supported. |
| `.modify speed` | `CharacterCommands.cpp`, `ChatHandler::HandleModifySpeedCommand`: selected player's run speed rate, client speed order/ack, cap at 4 below basic admin | In-flight targets are refused (`LANG_CHAR_IN_FLIGHT`); the target is told (`LANG_YOURS_SPEED_CHANGED`). |
| `.modify scale` | `UnitCommands.cpp`, `ChatHandler::HandleModifyScaleCommand`: set a unit's object scale, then `Unit::UpdateModelData` (`SpellSystem.UpdateDisplayModel` when client model data is loaded, else the bounding radius, combat reach and collision height follow the scale's ratio) | The change is current-life state; subsequent model replacement may recalculate geometry. |
| `.npc set flag` | `CreatureCommands.cpp`, `ChatHandler::HandleNpcSetFlagCommand`: change the selected creature's NPC service flags | Current creature only; spawn template and DB rows are not edited. |
| `.go creature`, `.go object` | `TeleportCommands.cpp`, `ChatHandler::HandleGoCreatureCommand` / `HandleGoObjectCommand`: find spawn GUID, template entry, or name and teleport the invoker | A creature of the spawn loaded on the caller's map gives its live position (TeleportCommands.cpp:498-503); objects use the stored spawn position, as vmangos. Not found replies `Creature not found!` / `Object not found!`. |
| `.reset talents` | `CharacterCommands.cpp`, `ChatHandler::HandleResetTalentsCommand`: no-cost talent wipe for an online player | Offline login flag is not available; requires the configured Talent catalog. |

Every player-targeted mutation uses `CommandContext.CanActOn`. The vmangos command table retail levels are specified on each new command. Replies use the vmangos `mangos_string` texts (mangos-classic `sql/base/mangos.sql`). The test fixtures are synthetic; no private live realm was started or mutated.

## Remaining high-value paths

ArcaneCore already has `.learn`, `.unlearn`, `.cooldown`, `.unaura`, `.revive`, `.go xyz`, `.lookup item|creature|object`, and `.instance listbinds|unbind`; the vmangos subcommand variants of some are still in the gap list. `.tele add|del` needs a persistent `game_tele` write path: the current `WorldMaps.ReplaceGameTeles` only replaces in-memory rows. `.modify faction` and `.npc set faction` need validated faction-template data; `FactionTemplate.dbc` is not in the supplied effective DBC directory. `.reset spells|stats` and battleground control commands need their full state transitions and separate acceptance. The vmangos table has `.npc spawn set standstate` and `.unit show standstate`, rather than a `.modify standstate` path.

The loopback tests prove command dispatch and the specified effects with synthetic content. Real-client packet acceptance, persisted teleport edits, and a full ClassicDB-loaded command run remain unverified.
