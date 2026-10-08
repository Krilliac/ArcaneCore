# Area: Mage, priest and warlock mechanics

Branch `claude/vw2-class-casters` (wave 2, lane `class-casters`). The shared-file edits and the merge notes are in
[docs/integration/casters.md](../integration/casters.md). Directive: everything as close to retail 1.12.1 as possible,
mechanics and data, verified against the vmangos reference (`D:\refs\vmangos`, primary), cmangos-classic, wow_messages
and classic-db. Nothing from those trees is copied: the code is a re-implementation, the tests carry a handful of
numeric oracle constants quoted with their source, and every formula cites `file:line` in its doc comment.

## Delivered

All new code is in `src/ArcaneCore.Game/Spells/Casters` (plus `src/ArcaneCore.World/Spells/Casters/CasterFeature.cs`).
`CasterSpellModules.Register(SpellSystem, CasterOptions?)` installs everything through the public seams
(`RegisterAura`, `AmountModifier`); the world daemon calls it from `CasterFeature` with the `Spells:Casters` settings.

| Slice | What it does | Reference |
|---|---|---|
| spell-bonus-math | Pure maths of the 1.12 spell power coefficient: cast time used for the bonus (clamp 1500-7000 ms, DoT 3500, direct/over-time split of combined spells, area and leech halving, 5 percent per extra effect with per-step integer truncation), default coefficient, aura tick count, the Nostalrius level penalty `1 - (20 - level) * 0.0375` for levels 1-20, explicit-coefficient precedence, and the done/taken combiners. | `SpellEntry.cpp:517-634, 756-777`; `SpellCaster.cpp:1457-1812`; `Unit.cpp:5175-5260, 5328-5400` |
| mana-spend-rule | The five second rule. A paid, non-triggered mana cast starts the 5000 ms timer unless the spell has Ex2 `DONT_BLOCK_MANA_REGEN` (0x02000000); the timer is held while the unit channels the spell that took the mana (patch 1.7). The power cost now includes the school percent multiplier (aura 72 writes `UNIT_FIELD_POWER_COST_MULTIPLIER`), aura 73 writes the flat modifier field, and `SCALES_WITH_CREATURE_LEVEL` spells divide by the level ratio. | `Spell.cpp:5051-5080, 6955-7040`; `Unit.cpp:235-253`; `SpellAuras.cpp:5391-5412` |
| channel-trigger-targets | A periodic-trigger channel whose aura sits on its caster (Arcane Missiles) casts the triggered spell at the channel target instead of the caster. The target is captured on the aura holder when the channel starts, because the final tick runs after the channel fields were cleared. | `SpellAuras.cpp:1519-1536` |
| spell-bonus-wiring | `SpellSystem.AmountModifier` (null by default) is consulted by direct damage, heal and health-leech effects, when a periodic aura is created (caster-side snapshot: patch 1.10 for damage and leech, 1.11 for heal) and on every periodic damage/heal tick (target side, with the stack count, dithered like `rand_ditheru`). `SpellBonusModule` reads ModDamageDone, ModHealingDone, the spirit-based stat percent auras, the percent done auras, ModDamageTaken, ModDamagePercentTaken, ModHealing and ModHealingPct with the 1.12 school-mask matching. Restored auras keep their saved amount (they never pass through the effect path). | `SpellAuras.cpp:4306-4466, 5878-5890, 6106-6140`; `SpellCaster.cpp:1457-1700` |
| drain-leech-auras | PERIODIC_LEECH (Drain Life, Siphon Life, Devouring Plague): bonus-snapshotted amount, target-side modifiers, the signed periodic (DOT) resist (a vulnerability adds damage), capped at the target's health, the living caster heals `int(dealt * multiplier)`, the channel ends when the target dies, periodic damage log. PERIODIC_MANA_LEECH (Drain Mana): drains `min(power, amount)` of the aura's power type, the caster gains it times the multiplier when it has that power, Improved Drain Mana (17864 / 18393) adds 15 / 30 percent as shadow damage (also through the DOT resist), damage-cancelled auras break. Packet builders in `CasterPeriodicPackets`. | `SpellAuras.cpp:5927-6000, 6116-6200`; wow_messages `smsg_periodicauralog.wowm`, `smsg_spellnonmeleedamagelog.wowm` |
| dispel-fidelity | `SPELL_EFFECT_DISPEL` follows vmangos: the type mask from the misc value (negative = magic, curse, disease and poison), the friend/enemy polarity only for magic and poison, the effect value is the removal count (at least one), every stack is one removal, a resist seam (`SpellSystem.DispelResistChance`, the talent spell mod), SMSG_SPELLDISPELLOG and SMSG_DISPEL_FAILED. The cast check uses the same candidate set. | `SpellEffects.cpp:2456-2600`; wow_messages `smsg_spelldispellog.wowm` (1.12), `smsg_dispel_failed.wowm` |

## Corrections to the reviewed design

- The design said a spell damage type `DIRECT` never gets spell power. In vmangos `DIRECT_DAMAGE` is plain weapon damage
  and `SPELL_DIRECT_DAMAGE` (spells and class abilities) does; `SpellBonusKind.SpellDirect` is the latter.
- The leech halving applies to HEALTH_LEECH and PERIODIC_LEECH only, not to PowerDrain, PowerBurn or PERIODIC_MANA_LEECH.
- Dispel Magic on a friendly target removes harmful auras (buffs only from an enemy); the design text had it reversed.
- A Renew-like snapshot is not exactly representable on a rounding boundary: `40 + 100 * 0.2 * 0.55` evaluates to 50.99999 in float
  arithmetic and truncates to 50, exactly as vmangos does; the tests use a benefit that is not on a rounding boundary.

## Limits (documented, not hidden)

- **Coefficients run formula-only.** vmangos stores a final per-effect coefficient in `spell_template.effectBonusCoefficient1..3`
  (GPL data, not in this repository). Without it the default formula is used: direct + DoT spells (Fireball, Pyroblast,
  Immolate, Holy Fire: 36 caster ids) and low ranks differ slightly from retail. `ISpellBonusCoefficients` is the seam for a
  loaded table; the importer slice (`spell-bonus-data`) is not delivered. The cmangos `spell_bonus_data` table is not a valid
  substitute (rank 1 rows carry max-rank coefficients).
- A unit standing in for a game object (`SpellSystem.CastForGameObject`) has no done side: `SpellBonusModule` skips the done
  bonus and the absorb-shield bonus for it, and the crit rolls of direct damage and healing are skipped (a game object has no
  auras and never crits, `SpellCaster.h:320`); the target side still applies.
- Spell power does not yet model: damage done versus creature types, equipped-item restricted auras, class script modifiers,
  talent spell mods, Ignite, totem/pet owner bonuses, weapon-based periodic damage (left to the melee formulas), absorb.
  Spell crit from intellect and the spell hit/resist tables belong to the stats lane (`VanillaSpellCombatRules` keeps its
  flat 5 percent), so caster damage is not retail until that lands.
- Five second rule: the consumers of the timer (mp5, aura 134 interrupt percentage, Evocation, Mage Armor) are in the stats
  and spell-breadth lanes. Until they merge a caster regenerates 0 mana for 5 s after any paid cast, as a gearless retail
  caster does, with none of the offsets.
- Triggered casts do not start the timer (vmangos skips it for casts triggered by an aura).
- Leech: no absorb (framework in another lane), no immunity checks, no Health Funnel (PERIODIC_HEALTH_FUNNEL shares the vmangos
  leech block; the target is the caster's pet: pets lane), no per-second channel cost framework.
- Dispel: no Shield Slam gate (warrior lane), no Warlock Spellstone polarity override, no charm priority, no reflected dispels.
- `AmountModifier` and `RegisterAura` are single slots: a second lane registering the same type replaces the first silently
  (`CasterSpellModules.Register` throws instead when another amount modifier is already installed).

## Not delivered (slice, reason)

`spell-bonus-data` (World schema table + importer: needs the user-supplied vmangos dump, no data in the repository),
`soul-shards` (needs a tap provider from stats / group-loot), `portals-and-spellcaster-objects`, `ritual-and-player-summon`,
`blink-leap` (navmesh primitive), `absorb-class-rules`, `class-spell-scripts`, `caster-aura-rule-acceptance` (other lanes'
primitives), `levitate-and-fall-flags` (vmangos tracks pending movement changes and relays the ack to observers; needs a
real-client pass and a movement-change ledger), `caster-acceptance-and-docs` beyond this document.

## Configuration

`Spells:Casters:Bonus:Enabled` (default `true`, the retail behaviour; `false` keeps the raw spell data amounts, for debugging only).

## Schema

None. No Characters or World table is added by this lane.

## Wave-2 integration note
Two primitives were implemented twice and unified: (1) **dispel** stays the combat/CC lane's implementation
(`SpellSystem.Dispel.cs`, `SpellModifiers`/`ISpellModifiers` resist chance, charm priority, spellstone); this lane's dispel partial
and `DispelPackets` were removed and its dispel tests now run against the merged code; `SpellSystem.DispelResistChance` no longer
exists. (2) **Drain/leech auras (53, 64)** are registered once by the built-in `LeechAuras` module, which delegates to this lane's
`Drain.DrainAuras` (spell power, Improved Drain Mana); the spell-breadth lane's duplicate ticks were removed. `CasterSpellModules`
no longer registers the drain auras itself. The spell-power amount seam (`AmountModifier`) and the combat lane's `ISpellModifiers`
are different seams and coexist.
