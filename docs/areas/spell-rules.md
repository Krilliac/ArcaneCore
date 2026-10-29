# Area: Spell combat rules, crowd control and diminishing returns

Branch `claude/vw2-combat-cc-spell-rules`. The code lives in `src/ArcaneCore.Game/Spells/Rules/**` (pure rules and
seams), `SpellSystem.Rules.cs`, `SpellSystem.Dispel.cs`, `SpellSystem.Pushback.cs` and `SpellSystem.Mitigation.cs`
(partial files of the spell system) and `src/ArcaneCore.World/Spells/SpellRules*Feature.cs` (configuration and wiring).
Standing directive: everything as close to vanilla 1.12.1 as possible, mechanics and data. Every formula cites the
reference it was re-implemented from; no code or data was copied (the references are GPL, read-only).

References (`<refs-root>`): **vmangos** is primary; **mangos-classic** and **classic-db** are used where vmangos has no answer;
**wow_messages** for packet layouts. Citations below are `file:line` of those checkouts.

## Delivered

| Slice | What | Main files |
|---|---|---|
| Foundation | `SpellMechanic` (vmangos SpellDefines.h:659-695), `DispelType` and masks, school masks, raw attribute bits, `SpellInfo` mechanic/area views, boss-relative levels (`SpellCaster.cpp:70-114`), `SpellRuleOptions`, `ISpellModifiers` / `ISpellCritSource` seams, 1.12 packets SMSG_SPELLORDAMAGE_IMMUNE / SMSG_SPELLDISPELLOG / SMSG_DISPEL_FAILED (`wow_messages` smsg_*.md) | `Rules/SpellMechanic.cs` ... `Rules/SpellRulePackets.cs` |
| Hit, crit, binary, resist chance | Magic hit chance (`SpellCaster.cpp:772-880`): 22% base floor, boss levels, victim attacker-hit auras, AoE avoidance, mechanic and debuff resistance, ALWAYS_HIT, IGNORE_RESISTANCES, binary scaling, talent resist-miss mod, 1-99% clamp. Resist chance (`SpellCaster.cpp:882-925`): penetration, vulnerability, innate creature resistance scaled by level/63. `IsBinary` is the vmangos list (`SpellMgr.cpp:3342-3393`). Crit (`Unit.cpp:5212-5316`, `SpellCaster.cpp:958-1024`): CanCrit needs a damage/heal effect, creatures never crit unless configured, potions and healthstones 10%, victim attacker-crit auras (spell, melee and ranged), melee/ranged-class crit from `GetUnitCriticalChance` (`Unit.cpp:2552-2595`), melee-class spells always crit a player who is not standing, exact crit damage/heal amounts | `Rules/MagicHitChance.cs`, `SpellResistance.cs`, `SpellBinary.cs`, `SpellCritRules.cs`, `SpellCombatRules.cs` |
| Crowd-control state | Stun, root, silence, pacify, pacify+silence, disarm, fear, confuse set their unit flags from the live auras (never toggled); interrupts (`SpellAuras.cpp:3444-3463, 3502-3545, 3548-3640, 3859-3890, 5625-5640`, `Unit.cpp:9112-9174`); stun stands a player up when not mounted and releases loot; totems ignore fear/confuse; PREVENTS_FLEEING blocks fear; the logout stun/root is a separate source so a logout cancel cannot lift a stun or root held by an aura | `Rules/CrowdControl/*`, `Player.cs` (`StunnedByAura`, `RootedByAura`) |
| Mechanic resistance and diminishing returns | `ISpellApplicationRule` hook in `ApplyEffects`; per-effect mechanic resistance (`Unit.cpp:2461-2470`); diminishing returns (`Spell.cpp:1733-1800`, `Unit.cpp:7618-7690`, `SpellEntry.cpp:281-390`): groups, level read at hit, first hit creates the entry at level 2, 100/50/25/0%, type PLAYER needs player-like sides, stuns diminish on creatures, Frost Shock in its own slowing group (patch 1.4.0), 15 s window from the LAST aura of the group ending, death clears, fully diminished holders are dropped (their damage effects still run) | `Rules/Application/*`, `Rules/Diminishing/*` |
| Immunities | School, damage, dispel, mechanic, mechanic-mask, effect and state immunity read from live auras (`Unit.cpp:5404-5658`); polarity rule, NO_IMMUNITIES / NO_SCHOOL_IMMUNITIES / IGNORE_CASTER_AND_TARGET_RESTRICTIONS bypass; IMMUNITY_PURGES_EFFECT purges (`SpellAuras.cpp:4042-4180`) and UNIT_FLAG_IMMUNE; MISS_IMMUNE in the hit roll in vmangos order (`SpellCaster.cpp:169-227`); immune effects stripped from the effect mask; immune DoT ticks send SMSG_SPELLORDAMAGE_IMMUNE; creature static masks through `ICreatureImmunityProvider` (`Creature.cpp:2438-2480`) | `Rules/Immunity/*` |
| Dispel | `SpellEffects.cpp:2456-2610`: type mask (7 or negative = magic/curse/disease/poison), polarity only for magic and poison, spellstone ignores faction, N picks (0 = 1) each one stack, talent dispel resistance, Shield Slam 50%, SMSG_SPELLDISPELLOG once, SMSG_DISPEL_FAILED; `DispellableAuras` shares the candidate function so NOTHING_TO_DISPEL agrees | `SpellSystem.Dispel.cs`, `Rules/Dispel/DispelRules.cs` |
| Caster-state gate | `Spell::CheckCasterAuras` (`Spell.cpp:6565-6672`): stunned (instant spells and stun-flagged casts), confused, fleeing, silenced (also interrupt lockout, not tested while stunned), pacified; immunity-granting spells (PvP trinket, Divine Shield shapes) cast through the states they cover | `Rules/Gating/CasterAuraGate.cs`, `SpellSystem.cs` (`CheckCast` hunk) |
| Pushback, interrupt, lockout | `Spell::Delayed` / `DelayedChannel` (`Spell.cpp:7465-7545`, `Unit.cpp:900-947`): 1000/800/600/400/200/200 ms, players only, resisted by the not-lose-casting-time mod and RESIST_PUSHBACK, packet to the caster only, DoTs never interrupt or delay; InterruptCast preconditions (`SpellEffects.cpp:3560-3600`); lockout kept not extended, blocks only silence-prevention spells (`SpellCaster.cpp:2503-2534`), silence-immune creatures ignore it (`Creature.cpp:3272-3277`), players get a cooldown for each known spell of the school (`Player.cpp:22312-22345`) | `SpellSystem.Pushback.cs` |
| Absorb, mana shield, split | `Unit::CalculateDamageAbsorbAndResist` (`Unit.cpp:1920-2200`, 1.12 order): school-immune units absorb all, school absorb (aura order, charges), mana shield (mana per damage, dithered), flat then percent split to the living aura caster, dealt by the attacker as damage over time (`Unit.cpp:2138-2140, 2176-2179`): no combat link and no PvP flag unless the aura's spell is a channel (`ShouldEnterCombat`, `Unit.cpp:696-717`), logged by the attacker with `SPELL_HIT_TYPE_SPLIT`, periodic when the split hit was a DoT tick; public `SpellSystem.AbsorbDamage` for the melee lane | `SpellSystem.Mitigation.cs` |

## Configuration (`SpellRules` section; every default is the retail behaviour)

| Key | Default | Meaning |
|---|---|---|
| `MagicHitFloorPercent` | 22 | Lowest base magic hit chance (vmangos `SpellCaster.cpp:829-834`, from a classic duel test). 1 reproduces cmangos-classic (`Unit.cpp:3861`) |
| `WorldBossLevelDiff` | 3 | Levels a world boss counts above its target (`World.cpp:744`): the spell hit and resist level difference, and (through `CombatEnvironment.WorldBossLevelDiff`) the defense skill-up of a world boss's white swing. Limit: the melee hit table's skill maximum (`MeleeHitTable.SkillMaxForLevel`, reached through the shared `CombatHooks`) and stealth detection still use the constant 3 |
| `CreatureSpellCrit` | false | Creatures that are not player-owned never crit with spells (`Unit.cpp:5216-5219`); true restores a 5% crit |
| `ResistTablePath` | unset | File with the retail partial-resist outcome table (`Unit.cpp:1885-1918`); unset uses the quarter-step approximation |
| `IgnoreHolyResistance` | false | vmangos resists holy only through the innate level resist, its holy field stays 0 (`Unit.cpp:1936-1946`); true is the cmangos rule (`Unit.cpp:3917`) |
| `DiminishingReturns` | true | Crowd-control diminishing returns |
| `DiminishingResetMs` | 15000 | Window from the end of the last aura of a group to the level reset (patch 1.4.0: "within 15 seconds of the *end* of the duration"; `Unit.cpp:7630`) |
| `DiminishingResetSweepMs` | 0 | 0 resets as soon as the window has passed (retail, patch 1.4.0). A positive value only resets on the clock's multiples of it, so the reset lands 15 to 15 + sweep seconds after the end (the unsourced "15-20 s, checked every 5 s" reading of Classic Era: set 5000) |
| `FrostShockDiminishesAsRoot` | false | Frost Shock is in its own PvP "slowing effect" group (patch 1.4.0); true puts it in the controlled-root group with Frost Nova and Entangling Roots, as vmangos does (`SpellEntry.cpp:333-343`) |
| `ImmunityEnforcement` | true | false disables every immunity check (development hosts) |
| `SpellBatchWindowMs` | 0 | Spell batching (`SpellSystem.Batching.cs`). A player's non-triggered cast that goes off (an instant, or a finished cast bar) is checked, leaves its cast slot so nothing in the window can interrupt it, and resolves at the window boundary with every other cast of that window, in arrival order; its go skips the caster's own state, range and line of sight (judged when it entered the batch). Two players who kill, stun or charge each other in one window both get their action. Retail batched (Kaivax, Blizzard, 2019: [forum 137118](https://us.forums.blizzard.com/en/wow/t/137118/1)); Blizzard's Classic servers used 400 ms until 1.13.7 ([forum 838945](https://us.forums.blizzard.com/en/wow/t/838945)). Opt-in while it is verified against a client: 0 resolves every cast at once, 400 is the retail value. Batches close at the same instants everywhere (`now / window`). A finished cast bar stays in its cast slot until its go (the unit reads as casting); an instant leaves it, and a held cast blocks a second request of its cooldown. Not batched: triggered, game-object, channel, next-swing, auto-repeat and item-equip casts, and periodic ticks. The one triggered exception is an item's second and later use spells: they wait behind its held first spell, so the item's spells still go off together in template order. A test run sets the window for a whole test assembly with `ARCANECORE_TEST_SPELL_BATCH_MS` |
| `SpellBatchCreatures` | true | With a window set: creature, pet and totem casts are batched like players' |
| `SpellBatchMelee` | true | With a window set: white swings (and extra attacks) are held and resolve at the same boundaries (`MapCombat.SwingBatch.cs`), so two units that kill each other with one swing in a window both land it; a held swing of a player who stopped attacking in the window still lands but does not start the auto-attack again |
| `SpellBatchAuraExpiry` | true | With a window set: a timed aura that runs out stays (still applied) until the first update of the next batch, so it ends with its batch: a stun that ends mid-batch lets its target act only from the next one. Channel auras end with their channel |

`SpellRulesCoreFeature` binds the section, installs `VanillaSpellCombatRules` with the options (unless another area
registered its own `ISpellCombatRules`) and the application rules in order: immunity, mechanic resistance, diminishing
returns. Features of this area live in namespace `ArcaneCore.World.Spells` so they attach after `SpellFeature`
(features attach by ordinal full name; a `...Spells.Rules` namespace would sort before it). A world test pins this.

## Deliberate deviations from the references (all documented in code too)

- **Partial-resist distribution is external data.** The vmangos distribution is a 31-row table (`Unit.cpp:1885-1918`)
  that is data and is not reproduced here. Set `SpellRules:ResistTablePath` to a file with the rows
  (`resist100,resist75,resist50,resist25,resist0,chanceResist`, `ResistOutcomeTable`) and the roll is retail: linear
  interpolation between the two rows around the chance, one roll, 100% rounded down to 75% (`Unit.cpp:2427-2458`). Without
  the file `RollResist` draws the two adjacent quarter steps so the mean equals the resist chance; that approximation is the
  only part that differs from retail, and only until the operator supplies the table. The DoT one-tenth rule with its four
  exempt spells (`Unit.cpp:2406-2425`) and the vulnerability extra damage (`Unit.cpp:1948-1953`, `2229-2230`) are code and
  apply in both modes.
- **Absorbed damage still breaks control.** A hit that is fully absorbed calls `OnDamageTaken(..., absorbed)`, which takes
  the vmangos `damage == 0` branch (`Unit.cpp:733-746`): damage-cancels auras break and a player's damage-cancels cast is
  interrupted (not by DoTs), with no pushback.
- **Melee and ranged spell crit.** Ports `GetUnitCriticalChance` (`Unit.cpp:2552-2595`): player crit field alone for
  players, 5 plus MOD_CRIT_PERCENT otherwise, the victim's attacker melee/ranged crit auras and the weapon-skill versus
  defense term. The skills come from the combat hooks (a player with no ranged weapon has weapon skill 0, as in vmangos).- **Triggered-by-aura.** `DiminishingClassifier` takes "triggered by an aura" (stun and root get their own trigger groups,
  `SpellEntry.cpp:341`) from `SpellCast.IsTriggered`; the engine has no proc system or aura-trigger flag yet.
- **Overlapping pacify/silence.** Flags are recomputed from all live auras of both aura types; vmangos clears the pacify
  flag when any pacify aura goes and checks only `MOD_SILENCE` for the silence flag. The overlap case is the only difference.
- **Passive auras and dispel type 0** are never dispelled (vmangos has no such guard; no retail dispel has misc 0).
- **Evade before positive.** `RollHit` keeps returning None for self casts and skips the evade check for positive spells
  (vmangos checks evade first for everything).
- **Positivity** uses `SpellInfo.IsPositive`, the engine's simplification of vmangos `IsPositiveSpell`; per-effect
  positivity uses the whole spell. Polarity decisions of immunities, dispels and the absorb of immune units depend on it.
- **Pushback packet** goes to the caster only, as in vmangos; the engine used to broadcast it.

## Limits (not implemented, by design of this lane)

- Spell power and healing power (+damage/+healing, coefficients, level penalty, ModDamageDone/ModHealingDone readers,
  DoT/HoT snapshot), the damage pipeline stages and once-per-target crit/log accumulation, block of melee-class spells.
  `SpellCombatRules.CritMultiplier` and `ISpellCritAmounts` are the only amount hooks.
- Spell reflection (REFLECT_SPELLS), deflect, heartbeat CC break resist and spell batching (Nostalrius extras).
- Dispel: the charm-priority pick (needs charm data); `Spell Reflection`-style reflect of dispels.
- Frost aura state on stun (needs the aura-state service), fear/confuse/charm and root movement generators
  (the movement code reads the `Fleeing` / `Confused` flags and `SpellSystem.IsRooted`), weapon-dependent aura mods and
  swing timers on disarm, stun clearing the target guid.
- `ISpellCritSource.Flat` gives every unit 5% crit until the stats lane merges the intellect-based chance
  (`Unit.cpp:2597-2640`, marked "must be checked" by vmangos). Crit on a sitting victim uses the stand state only.
- Creature immunity data: `ICreatureImmunityProvider` has no implementation; the creature template data (classic-db
  `creature_template.MechanicImmuneMask` / `SchoolImmuneMask`, 1,964 + 172 of 10,384 creatures, and
  `creature_immunities`) must be imported by the content lane; no schema is added here.
- Per-spell coefficient data (`spell_bonus`), DR for pets (pets lane: only players are "player-like" for
  diminishing returns), talents' `ISpellModifiers` implementation, item equip spells that grant spell power.
- Melee absorb call sites: `AbsorbDamage` is public; the melee lanes still have to call it from `MapCombat`.

## Provenance and open questions

- Holy: vmangos keeps the holy resistance field at 0 (holy is only resisted by the innate level resist), cmangos ignores holy resistance ("since beta stages"); the primary reference wins
  and `IgnoreHolyResistance` selects the other. Which one is real retail is not provable offline.
- The 22% hit floor comes from one vmangos duel test; cmangos floors the final chance at 1%.
- Polymorph, Mind Control, Sleep and Banish are not binary in the vmangos list, so resistance does not lower their hit
  chance (implemented as vmangos).
- `DIMINISHING_LIMITONLY` (curses, Hamstring) is never enforced by vmangos or mangos-classic; it is a no-op here too.
- DR window start: vmangos stamps the time when the last aura of the group is removed (`Unit.cpp:7676-7690`);
  mangos-classic restamps on every apply and remove. vmangos is used.
- Contested diminishing-returns items, settled against period evidence (2026-10-27). Emulators are not evidence:
  - Frost Shock is not a root for diminishing returns. Patch 1.4.0 (2005-04-19) lists Frost Shock as "a slowing effect"
    and Entangling Roots and Frost Nova as "an immobilizing effect"
    ([Patch 1.4.0](https://vanilla-wow-archive.fandom.com/wiki/Patch_1.4.0)). It stayed on diminishing returns until patch
    2.3.0 ([Diminishing returns, patch changes](https://vanilla-wow-archive.fandom.com/wiki/Diminishing_returns)). It is the
    `Slowing` group (type PLAYER: "in PvP"). vmangos files it under the root group although it quotes the same note.
    `FrostShockDiminishesAsRoot` restores that.
  - The reset is 15 s after the end of the last aura of the group (patch 1.4.0). The "15-20 s" figure for Classic
    ([warcraft.wiki.gg](https://warcraft.wiki.gg/wiki/Diminishing_returns), Classic section) has no Blizzard source.
    `DiminishingResetSweepMs` reproduces it.
  - PvP duration cap: 1.12 has none. Patch 1.2.0 made players and beasts "break free" of Fear, Sap, Hibernate and Scare
    Beast with an increasing chance, "such that it is unlikely the effect will last more than 15 seconds"
    ([Patch 1.2.0](https://vanilla-wow-archive.fandom.com/wiki/Patch_1.2.0)). That is the heartbeat resist, already
    delivered (aura engine). The hard cap ("will last no longer than 12 seconds") came with patch 2.0.1
    ([Patch 2.0.1](https://warcraft.wiki.gg/wiki/Patch_2.0.1)). No cap is applied.
  - Stun diminishing returns on creatures: unverified. Patch 1.9.0 split the proc stuns (Impact, Mace Specialization, ...)
    from controlled stuns ([Patch 1.9.0](https://vanilla-wow-archive.fandom.com/wiki/Patch_1.9.0)). Blizzard's Classic list
    confirms that split ([Not-a-bug list](https://us.forums.blizzard.com/en/wow/t/wow-classic-era-%E2%80%9Cnot-a-bug%E2%80%9D-list-updated-april-22-2021/175887)).
    No 1.x patch note or blue post found says whether stuns diminish on creatures. The vmangos behaviour (stun groups are
    type ALL) is kept.

## Tests

`tests/ArcaneCore.Game.Tests/SpellRules/*` (rules, tracker, classifier, immunity, dispel, gate, pushback, absorb tests)
and `tests/ArcaneCore.World.Tests/SpellRules/*` (feature order, option binding, installed rules, loot-release seam).
Existing tests that pinned non-retail behaviour were corrected: flat 500 ms / 25% pushback, the unfloored hit chance,
holy never resisted, damage class gating of partial resists, the exact-type dispel with polarity on every type.
