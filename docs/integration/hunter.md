# Integration: hunter lane (`claude/vw2-class-hunter`)

Delivered scope, limits and provenance: `docs/areas/hunter.md`. This note is for the integrator.

## Schema

- **Characters** module `CharacterAmmoDataModule` (table `character_ammo`: `CharacterId` int PK, `ItemId` uint), `CharacterAmmoDataModule.Version = 14`. It was the next free number after durable loot (13) in this tree; **renumber that one constant at merge**. It implements `ICharacterDataCleanup`.
- `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs`: one added line in the `expected` module table (`typeof(CharacterAmmoDataModule), DatabaseComponent.Characters, CharacterAmmoDataModule.Version`) and its `using`. Every other lane adds a similar line; merge by hand.
- No world (content) schema change.

## Shared files edited (all narrow, marked `ranged (hunter lane)`)

| File | Change |
|---|---|
| `Game/Spells/SpellSystem.cs` | `using ArcaneCore.Game.Ranged`; `Prepare`: `CastTimeFor`, `WithAmmoFlag` + `GetAmmoVisual` on SMSG_SPELL_START; `Cast`: `TakeAmmo` after `TakePower`, same on SMSG_SPELL_GO; `CheckCast`: after the movement check `CheckRangedItems` and `CheckStalkedTarget`, the dead-caster check and the range check skipped while `_objectCastDepth > 0`, `CheckRange` gets `movementLeeway` (+ `RangeLeeway.Bonus`); `UpdateCast`: a preparing player cast is cancelled while feigning; `IsSpellReady`: a `COOLDOWN_ON_EVENT` spell waits for its live object; `AddCooldown`: ranged attack time term (no early return when only that term is non-zero), `onEvent` parameter |
| `Game/Spells/SpellInfo.cs` | `GetCastTime(casterLevel, castSpeed, autoRepeat = false, rangedHaste = 1.0f)` |
| `Game/Spells/SpellPackets.cs` | `BuildSpellStart` / `BuildSpellGo` take an optional `AmmoVisual` (defaults write the old zeros) |
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | see above |

Everything else is new files: `Game/Ranged/*`, `Game/Spells/SpellSystem.{Ranged,Feign,Tracking,Objects}.cs` (partials of `SpellSystem`), `World/Ranged/*`, `Data/Characters/Ranged/*` and the tests.

## Seams the hunter lane exposes (for other lanes)

- `PlayerAmmo.CurrentDps(player)`: the stats lane adds it to the ranged min/max damage (vmangos `StatSystem.cpp:440-443`: `+= ammoDps * attackSpeed`) and re-evaluates ranged damage when slot 17 or the ammo changes.
- `UNIT_FIELD_RANGEDATTACKTIME` must be written for players (weapon `Delay`, with haste). The spell cooldown and Auto Shot read it; until then the ranged cooldown term is 0.
- `SpellSystem.RangedAttackSpeedPct` (`Func<Unit, float>`, default 1.0): the attack-speed aura owner (aura 140) installs the reader of `m_modAttackSpeedPct[RANGED_ATTACK]`.
- `SpellSystem.EquipSlotDurabilityLoss` (`Action<Player, byte>?`): the item mechanics lane installs `DurabilityPointLossForEquipSlot`; called once per throw of a non-stackable thrown weapon.
- `SpellSystem.IsFeigningDeath(unit)`: creature AI (target validity and aggro: vmangos `Creature.cpp:1179,1199` check the creature's own state; hostile aggro must skip a feigning player), movement (`UNIT_STATE_CAN_NOT_MOVE` contains the feign state) and the pet owner logic read it.
- `SpellSystem.BreakFeignDeath(unit)`: one line at the top of each handler vmangos guards with `if (HasUnitState(UNIT_STATE_FEIGN_DEATH)) RemoveSpellsCausingAura(SPELL_AURA_FEIGN_DEATH)`: auction house (six handlers), guild, item (buy / sell / use), NPC gossip and vendor, petitions, quest, skill / trainer, taxi.
- `SpellSystem.SpellObjects` (`SpellObjectRegistry`): owner, creating spell and slot of every spell-created game object. `SpellSystem.RemoveOwnedObjects(owner)` is vmangos `RemoveAllGameObjects`.
- `RangedHandlers.Register(system)` installs aura handlers 44, 45, 66, 68, 151 and effects `SummonObjectSlot1-4`; `RangedFeature` calls it. A lane that registers the same aura or effect later replaces it silently (`RegisterAura` / `RegisterEffect` overwrite), so check ordering when merging.
- `TrapSystem.IsHostileTo`: replace with a faction-reaction predicate (`IsHostileTo` as opposed to `IsValidAttackTarget`) so neutral creatures do not trigger traps.

## Hand-offs and open questions

- **Spell.dbc.** None of the reference repositories has it. Run the checklist at the end of `docs/areas/hunter.md` against the developer's client data before trusting any attribute bit (Auto Shot, the 0x8 exotic bit, the trap summon slots and `COOLDOWN_ON_EVENT`).
- **Auto Shot (H5)** waits for the spell-breadth lane's auto-repeat slot. Rules to implement there, from vmangos `Unit::_UpdateAutoRepeatSpell` (`Unit.cpp:2733-2778`): a moving player, or any non-auto-repeat cast in progress, raises the ranged timer to **500 ms only when it is below 500** (a higher timer stays); a wand cancels while the player moves or another category-351 spell is cast; a ready timer calls `CheckCast(strict)`, where `MOVING` and `NOT_READY` only delay and any other failure cancels (`IsAcceptableAutorepeatError`, `Spell.cpp:3329-3332`), then casts, resets the ranged timer to the attack time and stands the player up. `CheckCast` must return `MOVING` for an auto-repeat cast by a moving player (`Spell.cpp:5395-5403`). Selecting another unit retargets or cancels (`MiscHandler.cpp:416-428`), `SMSG_CANCEL_AUTO_REPEAT` (empty payload, opcode 668) tells the client. Send no `SMSG_SPELL_COOLDOWN` for the auto-repeat copy until vmangos behaviour is checked (the existing triggered-cast code would).
- **Original caster of a trap cast.** A trap cast needs an "original caster is a game object" marker on `SpellCast` so a player hit by a trap is not put in combat and the effect is never reflected (`Spell.cpp:946-950,1650`). `SpellCast.cs` is shared with the spell-breadth lane, so it was not touched; `CastFromObject` uses a depth counter on `SpellSystem` instead.
- **Environmental traps** (555 spawn rows in classic-db: Rookery Egg, Onyxia Egg, Roaring Flame, Suppression Device) need a game-object spell caster.
- **Hunter's Mark** needs the visibility owner (always visible to the caster) and the single-cast-per-caster registry (ends on caster death, `Unit.cpp:3969-4004`).
- **MockClient scenario.** The harness has no hunter character: its in-memory world data allows human / orc warriors only. A `MockScenarios.Hunter.cs` plus a call-site line in `MockScenarios.cs` needs that data first.

## Wave-2 integration note
`CharacterAmmoDataModule` and `character_ammo` no longer exist (see docs/areas/hunter.md, "Wave-2 integration note: ammo"); the
Characters allocation is in docs/integration/seams.md. `SpellInfo.GetCastTime` now has one signature carrying both this lane's
`autoRepeat`/`rangedHaste` and the spell-breadth cast-time modifier: `GetCastTime(level, castSpeed, autoRepeat, rangedHaste, castTimeModifier)`.
