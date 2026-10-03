# Area: hunter ranged combat and mechanics

Branch `claude/vw2-class-hunter`. Integration notes (shared-file edits, schema constant, hand-offs to other lanes): `docs/integration/hunter.md`.

Everything defaults to the vmangos / retail 1.12.1 behaviour; every deliberate deviation is a `Ranged:*` option whose default is retail. The reference repositories carry **no Spell.dbc** (classic-db `spell_template` holds eight stub rows), so no spell attribute, range index or trap summon slot could be verified from them. All spell tests use synthetic `SpellInfo` records; the checklist for the developer's client DBC is at the end.

## What is in

| Piece | Where | Reference |
|---|---|---|
| Options: `Ranged:Ammo:Mode` (`Retail`, `Infinite`), `Ranged:Range:Leeway` (`Retail`, `None`), `Ranged:Traps:RadiusSource` (`Vmangos`, `Template`), `Ranged:Traps:Hostility` (`Faction`, `AttackTarget`) | `Game/Ranged/RangedOptions.cs`, bound by `World/Ranged/RangedFeature.cs` | |
| Spell attribute facts: ranged slot 0x2, exotic ammo 0x8, Ex2 auto-repeat 0x20, Ex2 do-not-reset-combat-timers 0x20000, weapon attack type of a spell | `Game/Ranged/RangedSpellFacts.cs` | vmangos `SpellDefines.h:831,833,911,923`, `SpellEntry.cpp:435-455`, `SpellEntry.h:892-895,1067-1075` |
| Weapon kinds (bow 2, gun 3, thrown 16, crossbow 18, wand 19), ammo matrix (bow and crossbow take arrows, gun takes bullets), ammo DPS `(min+max)/2`, no-ammo spells 2094 / 13099 / 13119 / 23577, exotic flag 0x8 | `Game/Ranged/AmmoRules.cs` | vmangos `Spell.cpp:5129-5171,7390-7455`, `Player.cpp:7514-7569` |
| `PLAYER_AMMO_ID`: `CanUseAmmo` (dead, unknown item, not `INVTYPE_AMMO`, equipment requirements), `SetAmmo`, `RemoveAmmo`, derived `CurrentDps`, starting ammo | `Game/Ranged/PlayerAmmo.cs` | vmangos `Player.cpp:10100-10158,7514-7569,560-575` |
| CMSG_SET_AMMO; ammo restored at login; starting ammo at character creation; delete hook | `World/Ranged/AmmoHandlers.cs`, `AmmoFeature.cs`, `AmmoPersistence.cs` | vmangos `ItemHandler.cpp:988-1008`, `Player.cpp:14703,16501` |
| `character_ammo` (characters schema module, `CharacterAmmoDataModule.Version` = 14, provisional) with `ICharacterDataCleanup` | `Data/Characters/Ranged/CharacterAmmoDataModule.cs` | vmangos `characters.ammo_id` (`sql/characters.sql:94`) |
| Ranged weapon and ammo checks (`EQUIPPED_ITEM`, `NO_AMMO`, `NEED_EXOTIC_AMMO`) before the range check; one ammo / thrown weapon / durability point taken after `TakePower` | `Game/Spells/SpellSystem.Ranged.cs`, call sites in `SpellSystem.cs` | vmangos `Spell.cpp:5694-5702,3716-3718,5129-5171,7390-7455` |
| `CAST_FLAG_AMMO` with the projectile trailer: START flags 0x22, GO flags 0x120, display id and inventory type of the arrow / bullet / thrown weapon, or the weapon's own inventory type with display 0 | `SpellSystem.Ranged.cs` (`GetAmmoVisual`), `SpellPackets.cs`, `Game/Ranged/AmmoVisual.cs` | vmangos `Spell.cpp:4495-4620` |
| Cooldown of a ranged-slot spell includes `UNIT_FIELD_RANGEDATTACKTIME` unless Ex2 0x20000, also when the spell has no recovery of its own | `SpellSystem.cs` (`AddCooldown`) | vmangos `Player.cpp:22193-22197` |
| Cast time: no flat +500 ms for the auto-repeat spell, ranged haste for ability-class ranged spells (Aimed Shot) | `SpellInfo.GetCastTime(..., autoRepeat, rangedHaste)` | vmangos `SpellEntry.cpp:485-514` |
| Range leeway +2.66 yd when a player is involved and both units run laterally above 4.97 yd/s (cast start and landing); dead zone test guards | `Game/Ranged/RangeLeeway.cs`, `SpellSystem.CheckRange` | vmangos `Object.cpp:1890-1912`, `ObjectDefines.h:56-57`, `Spell.cpp:6911-6945`, `Unit.cpp:7112-7137` |
| Tracking auras 44, 45, 151 (player fields, mutual exclusion) and Hunter's Mark (aura 68: `UNIT_DYNFLAG_TRACK_UNIT`, attackable-target rule) | `Game/Ranged/TrackingAuras.cs`, `SpellSystem.Tracking.cs` | vmangos `SpellAuras.cpp:2909-2940,4217-4226`, `Spell.cpp:6436-6447`, `SpellEntry.cpp:148-157` (a spell is a tracker only with `NO_AUTOCAST_AI` or `ALLOW_WHILE_MOUNTED`; Mind Vision is exempt from the attackable rule, `Spell.cpp:6444-6447`) |
| Feign Death (aura 66) | `Game/Spells/SpellSystem.Feign.cs` | vmangos `SpellAuras.cpp:3460-3500`, `Unit.cpp:9228-9278,10181-10215`, `Spell.cpp:4082-4090` |
| `SummonObjectSlot1-4` effects and the ownership of spell-created objects (slot replacement, level, duration, owner leaves the map, `COOLDOWN_ON_EVENT`) | `SpellSystem.Objects.cs`, `Game/Ranged/SpellObjectRegistry.cs`, `SpellObjectSystem.cs` | vmangos `SpellEffects.cpp:5151-5226`, `Unit.cpp:4075-4173,8287` |
| Hunter traps: arming delay, nearest-target scan, cast as owner, cooldown, charges, custom animation, 2.5 yd override | `Game/Ranged/TrapSystem.cs`, `TrapRules.cs` | vmangos `GameObject.cpp:274-308,340-360,455-600,2431-2447` |

## Behaviour notes

- **Ammo and weapon checks** apply only to players and only to spells with a `WEAPON_DAMAGE` / `WEAPON_DAMAGE_NOSCHOOL` effect whose attack type is ranged (as vmangos). They run before the range check, so no ammo is reported before out of range. `Ammo:Mode=Infinite` still requires a ranged weapon but neither ammo nor consumption.
- **Ammo DPS** is derived on every read (`PlayerAmmo.CurrentDps`) instead of cached as vmangos' `m_ammoDPS`, so it cannot go stale when the weapon or the ammo changes. The stats lane adds it to the ranged damage fields (vmangos `StatSystem.cpp:440-443`).
- **PLAYER_AMMO_ID is not validated at login** and is not cleared when the stack runs out (vmangos has neither).
- **Feign Death** (aura 66). On apply, every creature that has the feigner in its threat list and is within its attack distance rolls the magic hit chance of the level difference (at least 22%, plus the feigner's spell hit modifiers, clamped to 1-99%); a hit resists the feign: `SMSG_FEIGN_DEATH_RESISTED` and `SMSG_CANCEL_COMBAT` go to the player, nothing else changes except the dead dynamic flag. Players holding the feigner in a threat list never resist (1.7.0). A success interrupts hostile casts aimed at the feigner (within 100 yd, cast bar with a cast time or channel), calls `CombatStop`, strips auras carrying `AURA_INTERRUPT_STEALTH_INVIS_CANCELS` and deletes the threat references. Movement flags are cleared. The feigner's own preparing cast finishes silently, a channel is cancelled. The cast bar of a player does not run while feigning. `SpellSystem.IsFeigningDeath(unit)` is the consumer predicate; `BreakFeignDeath(unit)` is vmangos `RemoveSpellsCausingAura(SPELL_AURA_FEIGN_DEATH)`.
- **Traps** fire only for objects a spell created and a unit owns (hunter traps with `data4` charges > 0). The trap casts its spell as its owner with a triggered cast that skips the range check and the living-caster check. The owner's PvP flag gates player targets. Defaults follow vmangos: arming delay `data7` always applied, cooldown `data5` or 4 s, `data4` charges then removal.

## Discrepancies and deliberate differences

- **Trap radius.** classic-db carries `data2 = 5` for the twelve hunter traps; vmangos overrides them to 2.5 yd in code (`GameObject.cpp:482-497`). Default `Ranged:Traps:RadiusSource=Vmangos` (2.5); `Template` uses `data2`.
- **Ammo check order.** The design note placed the ammo check after `CheckPower`; vmangos runs `CheckItems` before `CheckRange` and `CheckPower` (`Spell.cpp:5694-5721`). vmangos wins.
- **START flags are 0x22, not 0x120.** Only SMSG_SPELL_GO carries `Unknown9` (0x100) with the ammo flag.
- **No `FeignDeath:PlayerCanBeResisted`, `AutoShot:FireWhileMoving`, `RetargetOnSelection`, `Traps:PvpOwnerRule`, `Traps:OneActivePerSlot` options.** The pre-1.7 branches are compiled out in vmangos for this build and the others were invented. Retail behaviour only.
- **Leeway** uses the speed-based ability branch (`Spell::CheckRange` always calls `GetLeewayBonusRange(target, true)`); the flags-only branch is for melee auto attacks and is not implemented.
- **The attack time field.** `UNIT_FIELD_RANGEDATTACKTIME` is read as an unsigned integer (the repository stores attack times as integers); nothing writes it for players yet (stats lane).

## Limitations (left for other lanes or later)

Not done in this lane, with the primitive they wait on:

- **Auto Shot / wand Shoot / Throw (H5):** needs the spell-breadth lane's auto-repeat slot (`CMSG_CANCEL_AUTO_REPEAT_SPELL`, breakage matrix) and the stats lane's ranged attack time from the weapon with haste. The pieces it will use are in place: weapon and ammo checks, ammo use, the projectile trailer, the ranged cooldown term, `GetCastTime(autoRepeat)`, and the wind-up rules written down in `docs/integration/hunter.md`. `CheckCast` does **not** yet return `MOVING` for an auto-repeat cast by a moving player (vmangos `Spell.cpp:5395-5403`).
- **Ranged haste values** (`RangedAttackSpeedPct` seam, aura 140), **quiver haste (aura 141)** and its AmmoType 0 rule (needs the attack-time percent primitive and equip spells of equipped bags), Rapid Fire, Aspect of the Hawk, Trueshot Aura values.
- **Aspect / sting / tracker stacking classes** (`SpellSpecific`), Aspect of the Cheetah / Pack daze, Entrapment, Counterattack / Mongoose Bite reactive states, Raptor Strike / Wing Clip next-swing, Volley, Distracting Shot and Disengage threat, hunter `spell_bonus_data` terms: spell-breadth, warrior and content-import lanes.
- **Pets** (Mend Pet, Call / Revive / Dismiss Pet, Tame Beast, Beast Lore, Eyes of the Beast, Bestial Wrath, Intimidation) and the Feign Death pet-combat rule.
- **Hunter's Mark** "always visible to its caster" and "ends when the caster dies" (visibility owner, single-cast registry).
- **Feign Death consumers:** creature aggro-on-sight ignores the state until the creature AI reads `IsFeigningDeath`; the movement lane's can-not-move predicate and the roughly twenty interaction handlers (`BreakFeignDeath`) are listed in `docs/integration/hunter.md`. A creature owned by a player is not skipped in the resist loop (no owner concept yet).
- **Traps:** environmental traps (no owner; need a game-object spell caster), battleground traps, totems counting at a third of the radius, stealthed traps hidden from enemies (no stealth system), duel / free-for-all and pets or charmed units (no owner or charmer link on units) in the owner PvP rule, the trap's later damage over time putting a player in combat, and the reflection exemption of traps. The faction-aware `IsHostileTo` is `CombatHooks.IsHostileTo`: `FactionCombatHooks` supplies the template reaction (neutral creatures do not trigger traps; reputation and PvP parts of `GetReactionTo` are not modelled); without a faction catalog the default falls back to the attack-target relation. `Ranged:Traps:Hostility=AttackTarget` is the deliberate deviation that always uses that relation. A trap's direct hit no longer puts a player in combat (`Spell.cpp:1650`). Trap placement uses the first close-point candidate (no collision or occupied-position search, caster height).
- **Thrown weapon wear:** non-stackable thrown weapons call `SpellSystem.EquipSlotDurabilityLoss`, which stays unset until the item mechanics lane installs a handler.
- **Creature projectile trailer:** non-player casters send zeros (vmangos reads the virtual item slots).
- **No MockClient hunter scenario** and no failing-until-landed parity tests (ranged attack power, crit, `spell_bonus_data`): they depend on the stats lane and the harness.

## Verification checklist against the client Spell.dbc (tools/spell-import)

Not possible from the references; to run against the developer's 5875 client data:

- Spell 75 (Auto Shot): `Attributes & 0x2` (ranged slot), `AttributesEx2 & 0x20` (auto-repeat), `AttributesEx2 & 0x20000`.
- Ranged abilities (Arcane Shot 3044, Multi-Shot, Aimed Shot 19434, Serpent Sting 1978): `Attributes & 0x2`, effect types (the ammo check only applies to `WEAPON_DAMAGE` effects), `DmgClass == 3`.
- Wand Shoot 5019, Throw 2764, Shoot Bow / Crossbow / Gun: category 351 for wands, auto-repeat bit.
- Any `Attributes & 0x8` spell (needs exotic ammo).
- Trap spells (Freezing 1499, Immolation 13795, Frost 13809, Explosive 13813 and ranks): which `SummonObjectSlotN`, `MiscValue` trap entry, `Attributes & 0x02000000` (`COOLDOWN_ON_EVENT`), duration.
- Feign Death 5384: aura 66, attributes, positive or negative.
- Hunter's Mark 1130: aura 68, `TargetA`.
- Tracking spells: aura 44 / 45 / 151 and `MiscValue` (creature type masks).
- SpellRange indices of the ranged abilities (the minimum range of the dead zone; the design example of 5 yd is unverified, retail is commonly quoted as 8 yd).

## Tests

- `tests/ArcaneCore.Game.Tests/Ranged/`: `RangedRulesTests` (item and attribute facts), `PlayerAmmoTests`, `RangedCastPipelineTests` (checks, ammo use, packets, cooldown, cast time), `RangeLeewayTests`, `TrackingAuraTests`, `FeignDeathTests`, `SpellObjectOwnershipTests`, `TrapSystemTests`.
- `tests/ArcaneCore.Data.Tests/Ranged/CharacterAmmoStoreTests.cs`: round trip, replacement, zero removes, deletion cleanup, conditional queued removal.
- `tests/ArcaneCore.World.Tests/Ranged/`: `SetAmmoLoopbackTests` (CMSG_SET_AMMO, owner-only field update, persistence across relog, starting ammo, character delete), `RangedFeatureTests` (configuration binding, handler and map system installation).

## Wave-2 integration note: ammo

Ammunition state was implemented twice (this lane and item-mechanics). One implementation remains: the **item-mechanics lane's**
`PlayerInventory` (PLAYER_AMMO_ID, `CanUseAmmo`/`SetAmmo`/`RemoveAmmo`, `AmmoDps`, `ConsumeRangedAmmo`, `TryGetAmmoVisual`,
CMSG_SET_AMMO, the starting ammo, persistence in `character_item_state`, Characters schema 16). Removed from this lane: the
`character_ammo` module (`CharacterAmmoDataModule`), `AmmoFeature`, `AmmoHandlers`, `AmmoPersistence`, `PlayerAmmo.SelectStartingAmmo`
and their store/loopback tests; `EquipSlotDurabilityLoss` (the wear is `PlayerInventory.DurabilityPointLossForEquipSlot`).
`PlayerAmmo` is now a facade over the inventory. Kept here: the ranged weapon/ammo cast checks, Hunter's Mark target rule,
ammo trailer packets, range leeway, traps, tracking, Feign Death. `RangedAttackSpeedPct` is still the neutral default (not wired
to the stats lane's ranged haste).
