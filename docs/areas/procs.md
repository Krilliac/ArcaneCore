# Procs

Status: wave 2, lane `proc-engine`. Reference: D:\refs\vmangos (primary), checked against the build 5875 Spell.dbc rows in the world database.
The engine is a port of vmangos' proc system; every deliberate difference is listed under "Deviations".

## What happens on an event

A hit path describes what happened with a `ProcEvent` (vmangos `ProcSystemArguments`, SpellCaster.h:247-268) and calls
`SpellSystem.ProcDamageAndSpell(actor, event)` (SpellCaster.cpp:270-323). The engine

1. collects the actor's auras that the event can proc (attacker side), then the living victim's (victim side), before handling any of
   them (`ProcDamageAndSpellFor`, Unit.cpp:8917-9002): an aura of the event's own spell never procs, a PROC_COOLDOWN_ON_FAILURE aura on cooldown is
   skipped, an aura the event's actor applied at or after the event's time is skipped (so an aura the same hit put on, through a nested
   triggered cast, does not proc from it), and an aura that holds charged spell modifiers is left to
   the casts that spend them;
2. checks each one (`IsTriggeredAtSpellProcEvent`, UnitAuraProcHandler.cpp:239-499): the hard-coded 1.12 cases (Flurry on extra attacks, Sap,
   Eye for an Eye, Improved Lay on Hands, Wrath of Cenarius, Omen of Clarity, Inspiration, ADD_TARGET_TRIGGER, Elemental Mastery, Fear Ward),
   the spell's proc script, the `spell_proc_event` row or Spell.dbc procFlags against the event (`IsSpellProcEventCanTriggeredBy`,
   SpellMgr.cpp:441-505: school, family, procEx, cast end), kill credit, the self-proc rule, the weapon or shield the aura needs, the
   proc-from-procs rule, ONLY_PROC_OUTDOORS (map collision), ONLY_PROC_ON_CASTER, then the chance (CustomChance, PPM from the attack time on the
   attacker side, the CHANCE_OF_SUCCESS spell modifiers);
3. handles them (`HandleTriggers`, Unit.cpp:4245-4345): each effect the event's spell can proc (the row's family mask, else
   `Aura::CanProcFrom`) runs the spell's proc script or its aura type's handler; OK spends a charge (FAILED does with
   PROC_FAILURE_BURNS_CHARGE); the holder goes with its last charge.

`TriggerProccedSpell` casts a triggered spell as a cast made by the aura (no power, no procs of its own unless NOT_A_PROC) and puts the row's
hidden cooldown on it (`AddProcCooldown`, server side only).

| Hit path | Where | Flags (attacker / victim) | vmangos |
|---|---|---|---|
| White swing, after the hit table, before the packet and the damage | `MapCombat.AttackerStateUpdate` → `OnMeleeSwingResolved` | DEAL_MELEE_SWING + hand / TAKE_MELEE_SWING (+ TAKEN_ANY_DAMAGE) | Unit.cpp:1320-1560, 2263 |
| Weapon hit that affected the victim (not parried or dodged) | `MeleeWeaponHitDealt` → `OnMeleeWeaponHit`: item chance-on-hit, then damage shields | - | Unit.cpp:1774-1782 |
| Spell hit with damage, before the damage sink (a reflected hit adds PROC_EX_REFLECT to the hit bits) | `DealDirectDamage` (once per target) | `Spell::PrepareMasksForProcSystem` | Spell.cpp:627-815, 1380-1456 |
| Spell heal, before the heal | `DeliverHeal` (once per target) | same, the whole heal (overheal included) as the amount | Spell.cpp:1300-1352 |
| Spell hit without damage or heal (aura, utility) | end of `ApplyEffects` | same | Spell.cpp:1458-1532 |
| Spell miss (miss, resist, dodge, parry, immune, ...) | `Cast`, miss branch | same, outcome from the miss | Spell.cpp:1458-1532, Unit.cpp:8776-8832 |
| Cast end (CAST_END rows only; the main target's outcome only when it is one of the spell's targets) and casts with no unit target | `Cast`, before the effects | attacker only | Spell.cpp:3724-3749 |
| ADD_TARGET_TRIGGER | `Cast`, at finish (channels: at start) | - | Spell.cpp:4271-4315 |
| Periodic damage / heal tick, before the damage | `TickPeriodicDamage` / `TickPeriodicHeal` | DEAL / TAKE_HARMFUL_PERIODIC (+ TAKEN_ANY_DAMAGE, PERIODIC_POSITIVE) | SpellAuras.cpp:5902-5917, 6060-6085 |
| Leech tick (Drain Life, Siphon Life), before the damage; the damage carries the holder's reflected flag and costs no durability on a kill | `DrainAuras.TickLeech` | DEAL / TAKE_HARMFUL_PERIODIC (+ TAKEN_ANY_DAMAGE) | SpellAuras.cpp:5981-5999 |
| Improved Drain Mana's shadow tick (a PERIODIC_DAMAGE tick of the talent spell); the mana drain itself fires none | `DrainAuras.ImprovedDrainMana` | same | SpellAuras.cpp:6200-6206 → 5917 |
| Kill | `MapCombat.UnitKilled` → `OnUnitKilled` (SpellProcFeature) | KILL / HEARTBEAT | Unit.cpp:1102-1104 |
| Reflect | `RollSpellReflect` in the hit roll | - / TAKE_HARMFUL_SPELL, REFLECT | SpellCaster.cpp:197-212 |

## Built-in proc handlers (vmangos `AuraProcHandler[]`)

Every aura type without one procs as `HandleNULLProc` (OK).

| Aura type | Handler | vmangos |
|---|---|---|
| ProcTriggerSpell (42) | EffectTriggerSpell (Aegis of Preservation, Mana Drain, Talisman of Ascendance, Persistent Shield remaps); no extra-attack spell while extra attacks are pending (except 20178); Curse of Mending / Improved Lay on Hands on the victim; Rogue Setup on the main target only; positive spells on the owner, others on the victim | UnitAuraProcHandler.cpp:1147-1624 |
| ProcTriggerDamage (43) | the aura spell's hit roll (no reflect; a miss sends SMSG_PROCRESIST and counts), its effect value through the spell damage bonuses, non-critical spell damage | :1626-1676 |
| AddTargetTrigger (109) | effect 0 base points as a percent (Blizzard / 8); the victim casts the trigger on itself, the owner on itself for Wolfshead Helm, Frosty Zap, Relentless Strikes | :1799-1843 |
| ReflectSpellsSchool (74) | the spell's school must be in the aura's mask | :1778-1782 |
| ModRoot (26), ModPacifySilence (60) | break chance = damage / (25 x level - 150, 50 below level 9) | :1926-1940 |
| ModFear (7) | fear and turn only; players x 1/3, damage over time x 3; final damage over the same scale | :1942-2008 |
| ModInvisibility (18) | a positive, non-passive invisibility ends | :2010-2017 |
| ModResistance (22) | Inner Fire loses charges on real damage only | :1845-1858 |
| ModDamageDone (13) | the school mask must match (a swing is physical) | :1860-1924 |
| ModMeleeHaste (138) | Flurry's last charge stays on a crit | :538-548 |
| ModCastingSpeedNotStack (65), ModPowerCostSchool(Pct) (72, 73), MechanicImmunity (77), ModMechanicResistance (117) | filters | :1772-1797 |
| OverrideClassScripts (112) | script ids 4309 (Nightfall), 836/988/989 (Improved Blizzard), 4086/4087 (Improved Mend Pet), 3656 (Corrupted Healing); other ids count the charge | :1678-1770 |
| ModPowerRegen (85), ResistPushback (149) | CANT_TRIGGER | table |
| Dummy (4) | OK (charge counting); class scripts handle the rest | :550-1145 |

## Damage shields and reflection

`TriggerDamageShields` (Unit.cpp:1785-1849): each DAMAGE_SHIELD aura of the struck unit rolls its spell's hit (no reflect) and the attacker's
school immunity, then deals its amount through the bearer's spell damage bonus as spell damage that does not start combat, announced with
SMSG_SPELLDAMAGESHIELD (victim, attacker, damage, school). White hits and weapon abilities that dealt damage trigger it.

Reflection (`SpellSystem.Reflect.cs`): REFLECT_SPELLS plus every REFLECT_SPELLS_SCHOOL aura covering the spell's school is the percent chance to
turn back a reflectable spell (magic damage class, not an ability, not NO_REFLECTION, not NO_IMMUNITIES, not passive, not positive). The roll
runs in `VanillaSpellCombatRules.RollHit` after the immunities; the victim's reflect procs (TAKE_HARMFUL_SPELL with PROC_EX_REFLECT, the way
reflect auras spend their charges through a spell_proc_event REFLECT row) run, SMSG_SPELL_GO lists the target as a REFLECT miss, and the
effects land on the caster (`ApplyEffects(..., reflected: true)`). Game-object casts (hunter traps) are not reflected back (> 1.9.4). A
reflected aura holder carries `IsReflected`.

Duels: a reflected spell that would kill its own caster is cut to 1 health (`MapCombat.ApplyDuelClamp`, vmangos Unit.cpp:770-776) and the
duel ends with the caster as the loser; a finished duel removes reflected debuffs too (DuelService.Complete, Player.cpp:6762-6787).

## Damage break and procFlags crowd control

`Auras:ProcEngineBreaksDamageAuras` (default true) is vmangos' `checkProcFlags`: the damage break skips auras whose spell has procFlags; the proc
engine ends those. Checked against the build 5875 Spell.dbc rows (world database `spell_template`):

| Spell | AuraInterruptFlags | procFlags | charges | Ends by |
|---|---|---|---|---|
| Polymorph 118, Sap 6770, Gouge 1776, Freezing Trap effect 3355 | DAMAGE | 0 | 0 | the damage break |
| Druid Prowl 5215 / 6783 / 9913 | 0x3C07 (stealth family) | 0 | 0 | the damage break (also damage over time) |
| Wyvern Sting 19386 / 24132 / 24133 (MOD_STUN) | DAMAGE | TAKEN_ANY_DAMAGE, 100% | 0 | its damage proc (`Auras:DamageProcCancelsAura`) |
| Hunter pet Prowl 24450 / 24452 / 24453 | stealth family | melee swing and ability, both sides | 1 | its proc charge |
| Frostbite 12494 (MOD_ROOT) | 0 | damage events | 0 | the root break chance |

The id exemption the engine used before the proc engine (Wyvern Sting and druid Prowl spared from the damage break) is gone: druid Prowl has no
procFlags, so damage, damage over time included, breaks it like Stealth.

## Data: `spell_proc_event` (world schema 39)

`SpellProcEventDataModule` (vmangos/cmangos columns plus the build range), `SpellProcEventDumpImporter` (classic-db or vmangos dumps by column
name; classic-db Full_DB z2815 stores cooldowns in seconds, `ProcCooldownUnit.Seconds`; z2829 and vmangos store milliseconds), the rank fill
(`SpellProcEventContent.Resolve`: higher ranks inherit the first rank's row, only PPM rows may stand for a higher rank), `SpellProcFeature`
(loads it when a store is registered) and `.reload spell_proc_event`. Nothing is bundled: without rows every proc aura runs on its Spell.dbc
procFlags and procChance, which already covers most auras; PPM procs (Hand of Justice, Crusader-style talents), procEx-only procs (Shield Block,
Flurry's crit requirement, reflect charges) and family-filtered talent procs need the operator's rows.

Importing: `arcane-content-importer proc-events <dump>... --database <file>` (or `--provider` with `--connection-string`) replaces the table with
the dump's build-5875 rows in one transaction; `--cooldown-unit seconds` for classic-db dumps before z2829, `--dry-run` writes nothing. The
store loads only rows whose `build_min..build_max` holds 5875 (vmangos `WHERE 5875 BETWEEN build_min AND build_max`), so rows copied into the
table straight from a vmangos dump are filtered too.

The lane was given world 41 by the wave-2 plan and held 38-40 open with empty placeholder steps; the 2026-10-07 integration deleted every
placeholder and renumbered the wave-2 world steps down to 38-41, so this step is 39 (docs/integration/wave2-20261007.md).

## Seams for other lanes

- `SpellSystem.RegisterProcScript(spellId, IProcScript)`: vmangos `AuraScript::OnCheckProc` / `OnProc` for one aura spell (seals, Judgement of
  Light/Wisdom, Lightning Shield, Sweeping Strikes, Vengeance, Blessed Recovery, Pyroclasm, the dummy proc auras). Return null to fall back to
  the generic check or the aura type's handler. One script per spell.
- `SpellSystem.RegisterProcHandler(AuraType, AuraProcHandler)`: replace or add an aura type's handler (vmangos `AuraProcHandler[]`).
- `SpellSystem.ProcDamageAndSpell(actor, ProcEvent)`: offer an event from a new hit path (pet attacks, totems, game objects).
- `SpellSystem.TriggerProccedSpell` / `CastProcSpell`: cast like vmangos TriggerProccedSpell / CastCustomSpell with triggeredByAura.
- `SpellSystem.ProcEvents` (`ISpellProcEventCatalog`), `SpellSystem.IsOutdoors`, `SpellSystem.ProcEventProcessed` (diagnostics).

## Deviations (deliberate)

- No spell batching: procs run at once (vmangos `Spell.ProcDelay` 400 ms default runs most attacker procs one batch later). The apply-time rule
  keeps vmangos' `>=` but compares the millisecond clock (wrap-safe) instead of whole seconds: vmangos also skips an actor's aura applied
  earlier in the same wall-clock second, ArcaneCore only one applied in the same millisecond.
- CAST_END events never carry CRITICAL_HIT: ArcaneCore rolls a crit when an effect deals its damage or heal, after the cast-end procs; vmangos
  rolls it per target before (`target.isCrit`).
- A spell's heal procs fire before its first heal effect heals (vmangos sums the heal effects into one heal).
- `Auras:DamageProcCancelsAura` (default true): Wyvern Sting's sleep ends on its damage proc (a proc whose handler returned FAILED does not end it); vmangos has no proc handler for MOD_STUN and keeps
  it (tooltip: "Any damage will cancel the effect"). False is the literal vmangos behaviour.
- A proc-triggered spell is cast without the aura's cast item (vmangos passes it, which makes item auras' negative spells proc on their own).
- Damage shield bonuses use the direct-damage amount stage (caster and target side) where vmangos takes the bearer's done bonus and, for a
  creature bearer, the attacker's taken bonus.
- A spell's damage procs fire before its first direct damage effect deals damage (ArcaneCore deals each damage effect separately; vmangos sums
  them into one damage).
- Effect 70 EXTRA_ATTACKS stays unsupported (vmangos HandleUnused: one visual-only spell); proc-driven extra attacks are PROC_TRIGGER_SPELL auras
  triggering SPELL_EFFECT_ADD_EXTRA_ATTACKS spells (Hand of Justice, Sword Specialization, Windfury), which work.

## Limits (not delivered)

- PERIODIC_HEALTH_FUNNEL (pets lane) and POWER_BURN_MANA are unsupported auras, so their ticks fire no procs yet (vmangos SpellAuras.cpp:5928,
  6300-6354); absorbs of a leech tick are not modelled (DrainAuras).

- The class-specific cases inside vmangos' ProcTriggerSpell, Dummy and OverrideClassScripts handlers go through `RegisterProcScript`: Seal of
  Righteousness and Judgement of Light/Wisdom are delivered ([class-scripts](class-scripts.md)); Illumination, Lightning Shield, Pyroclasm, Shadowguard,
  Blessed Recovery, set bonuses and Sweeping Strikes are not yet.
- Wyvern Sting's wake-up damage over time (24131/24134/24135 on removal, vmangos spell_hunter.cpp) is a hunter class script.
- Pet melee and pet spells offer their events only through the same map combat and spell paths (no owner-side procs).
- SPELL_ATTR_EX4_CLASS_TRIGGER_ONLY_ON_TARGET (ADD_TARGET_TRIGGER on the selected target only) and SPELLMOD_CHARGES on new holders.
- Real data: the `spell_proc_event` rows (classic-db or vmangos dump) and the client's Spell.dbc; the tests use synthetic spells.

## Tests

`tests/ArcaneCore.Game.Tests/Procs/ProcEngineBehaviourTests.cs` (only pre-engine APIs: RED on 2ca2f4e1, 9/9),
`ProcEngineTests.cs` (rows, charges, shields, kills, reflect charges, break chances, seams), `ProcEngineFidelityTests.cs` (leech and Improved
Drain Mana ticks, the apply-time rule and the clock wrap, PROC_EX_REFLECT on reflected damage, heal procs before the heal, CAST_END alone,
the damage-proc cancel on a failed proc), `Auras/AuraInterruptEngineTests.cs`,
`Duel/DuelCompletionTests.cs`; `tests/ArcaneCore.Data.Tests/Procs/*` (import, rank fill, the build-range load and the schema step on every provider, the `proc-events` command);
`tests/ArcaneCore.World.Tests/Spells/SpellProcFeatureTests.cs` (load, reload, kills on a map) and the playerbot scenarios
`Playerbots/Scenarios/ProcScenarioTests.cs` (damage shield in a duel; reflected lethal bolt ends the duel at 1 health).
