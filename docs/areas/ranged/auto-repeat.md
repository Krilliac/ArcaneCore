# Ranged lane S02: the auto-repeat slot (Auto Shot, wand Shoot)

Branch `claude/vw5-ranged-combat`. Retail only: no option (the hunter lane forbids invented Auto Shot options).

## Data (classic-db `spell_template`, a full Spell.dbc dump)

Only two real auto-repeat spells exist (ranged slot 0x2 and AttributesEx2 0x20): Auto Shot 75 (Attributes 0x50012, Ex3 0x8000, category 0, instant, no global cooldown, interrupt flags 1, range index 114, projectile speed 40) and wand Shoot 5019 (Attributes 0x12, Ex3 0x408000, **category 351**, effect 17 WEAPON_DAMAGE_NOSCHOOL, damage class magic, interrupt flags 15, range index 4). Throw 2764 (category 76), Shoot Bow 2480, Gun 7918 and Crossbow 7919 are ordinary ranged abilities (Ex2 without 0x20) whose cooldown is the ranged attack time; they need no slot. Spells 1485 and 19881 are zz/TEST rows.

## Delivered

| Piece | Where | Reference |
|---|---|---|
| Third spell slot `UnitSpellState.AutoRepeatCast` plus `AutoRepeatFirstCast`. The master stays Preparing in its own slot, never in `CurrentCast`, and never casts itself. | `Spells/SpellCast.cs`, `Spells/SpellSystem.AutoRepeat.cs` | vmangos `Spell.cpp:4097-4113` (update skips `cast()` for IsAutoRepeat), `SpellCaster.cpp:1917-1992` |
| Press: generic cast on the bar gives SPELL_IN_PROGRESS (a channel does not block); `CheckCast(strict)` with MOVING and OK acceptable (toggle while moving); a replaced spell is cancelled with SMSG_CANCEL_AUTO_REPEAT; wand Shoot (category 351) interrupts a generic cast and a channel, Auto Shot breaks nothing; one SMSG_SPELL_START (flags 0x22 with the ammo trailer, cast time 0), global cooldown only if the spell has one. | `PrepareAutoRepeat` | vmangos `Spell.cpp:3329-3332,3379-3416,3440-3472`, `SpellCaster.cpp:1950-1962` |
| `CheckCast` returns MOVING for a moving player's auto-repeat press. | `SpellSystem.cs` | vmangos `Spell.cpp:5395-5403` |
| Shot cycle, run before the casts each update: target gone cancels; moving (players) or any other cast or channel cancels a wand and only restarts the wind-up for Auto Shot; the 0.5 s wind-up raises the ranged timer only when it is below 500; when the ranged timer is ready `CheckCast(strict, skip cooldown)`: MOVING and NOT_READY wait, any other failure cancels (the client is told SMSG_CANCEL_AUTO_REPEAT plus INTERRUPTED, not the reason), OK fires a triggered copy, restarts the ranged timer from the hasted weapon speed and stands the player up. | `UpdateAutoRepeat` | vmangos `Unit.cpp:2733-2778`, `Unit.cpp:2673-2674` |
| A generic non-triggered cast (instant abilities too) cancels a wand and gives Auto Shot a fresh wind-up. | `OnGenericCastStarted` | vmangos `SpellCaster.cpp:1936-1946` |
| Shots send no SMSG_SPELL_COOLDOWN (the general rule "every triggered player cast sends one" is left alone; the shot copies are flagged). Ammo is taken by the existing `TakeAmmo`. | `SpellCast.AutoRepeatShot`, `SpellSystem.Cast` | vmangos `Player.cpp:22139-22250` |
| `CancelAutoRepeat`: SMSG_CANCEL_AUTO_REPEAT (668, empty) to a player first, then the normal cancel (SMSG_SPELL_FAILED_OTHER, SMSG_CAST_RESULT interrupted). Used by CMSG_CANCEL_AUTO_REPEAT_SPELL (621, empty; `World/Ranged/AutoRepeatHandlers.cs`), CMSG_CANCEL_CAST with the spell id, death, `InterruptNonMeleeSpells` (knockback, pets), logout (`Forget`, silent). | `SpellSystem.AutoRepeat.cs` | vmangos `SpellHandler.cpp:439-444`, `SpellCaster.cpp:2068-2100`, `Player.cpp:17049-17052`; wow_messages `cmsg_cancel_auto_repeat_spell.wowm`, `smsg_cancel_auto_repeat.wowm` |
| CMSG_SET_SELECTION retargets or cancels (`RetargetAutoRepeat`). | `World/Handlers/PlayerHandlers.cs` | vmangos `MiscHandler.cpp:416-428` |
| The auto-repeat slot counts as casting for the melee hook, so with the retail gate of S03 no white swing fires and no timer is consumed while Auto Shot is on. | `Combat/Melee/MeleeSpellHooks.cs` | vmangos `SpellCaster.cpp:2003-2022` |

## Limits (recorded, not delivered)

* **Distances**: SpellRange.dbc is in no reference repository, so the dead zone (minimum range) of Auto Shot (range index 114), Throw (74) and wand Shoot (4) is unverified; tests give the spells an explicit range and do not pin distances. Read them from the client DBC with `tools/spell-import` before relying on them.
* Not wired: cancel on taxi start (`Player.cpp:17898-17906`, the taxi system does not call the spell system), `SPELL_ATTR_CANCELS_AUTO_ATTACK_COMBAT` (0x100000) casts (needs a melee `AttackStop` from the spell system), and `CombatStop(includingCast)`.
* No client was attached: the client's own toggle (Auto Shot bar) reacting to SMSG_CANCEL_AUTO_REPEAT, the 0.5 s wind-up feel and the dead-zone messages need a developer playtest.
* The pet's and creature's `Shoot` casts (EventAI, 188 creatures) stay ordinary casts; none of them is an auto-repeat spell.
