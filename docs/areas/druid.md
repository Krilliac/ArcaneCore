# Druid (class-druid lane)

Branch `claude/vw2-class-druid`. Code: `src/ArcaneCore.Game/Spells/Druid/` (pure rules),
`src/ArcaneCore.Game/Spells/Interrupts/` (aura/channel interrupt helpers and the high-liquid updater),
`src/ArcaneCore.World/Spells/LiquidInterruptFeature.cs` (world attachment). Tests:
`tests/ArcaneCore.Game.Tests/Druid/`, plus two taxi cases in `Npc/NpcTravelServiceTests.cs`.

Directive: as close to vanilla 1.12.1 as possible, verified against the references. Primary reference is
vmangos (`D:\refs\vmangos`); mangos-classic differs in several druid details (below) and is not followed.
Nothing from the references was copied into the repository, only numeric constants and behaviour with a
`file:line` citation in the source comment.

## Delivered

Wired into the running server: the power caps (Player constructor), the high-liquid updater (`LiquidInterruptFeature`)
and the taxi interlock. **Not wired (no runtime caller yet, exercised by tests only):** `PowerTypeSwitch.SetPowerType`,
`FeralFormulas`, `FurorRules`, `FormDisplayTable`, `FormBoostTable`, `RipDamageRules`, `FrenziedRegenerationRules`,
`ShapeshiftFormEffectRules`, `DruidForms` and the other pure tables. They are primitives for the form engine
(see "Not delivered"); until aura 36 exists, druid forms remain inert buffs and these rules change no behaviour.

### Power type switch (`PowerTypeSwitch`)
Port of `Unit::SetPowerType` (`Objects/Unit.cpp:4386-4427`) and the create-power maxima of `GetCreatePowers`
(`Unit.cpp:8245-8264`). Writes `UNIT_FIELD_BYTES_0` byte 3; Rage sets max 1000 and current 0, Energy max 100 and
current 0, Mana changes nothing else. `EnsureFeralPowerCaps` gives a druid the rage/energy maxima without touching
the type or current values, never lowering a maximum. It is called from the `Player` constructor (every class, as
vmangos `UpdateAllStats`), which fixes a real defect: a druid used to be created with only the
mana maximum set, so every rage or energy write clamped to 0 (`MapCombat.SetPower`). The group PowerType update
flag is produced by the existing `GroupManager` member diff, so nothing is sent from here.

### Pure form tables and formulas
- `DruidForms`: form ids (`SharedDefines.h:1421-1442`), stance mask `1 << (form - 1)`, `IsTankingForm`,
  `IsAttackSpeedOverridden`, form-id half of `IsInDisallowedMountForm` (`Unit.cpp:5870-5878`).
- `FormDisplayTable`: display id and scale per form and team (`SpellAuras.cpp:2317-2411`). Scale 0.8 for cat,
  travel and aquatic is the vmangos value (mangos-classic does not scale).
- `FormBoostTable`: linked boost spells per form, Leader of the Pack rule, Heart of the Wild lookup constants
  (`SpellAuras.cpp:5433-5530`).
- `FurorRules`: proc spells and rank amounts (`SpellAuras.cpp:2512-2548`). Ranks are 17056, 17058, 17059, 17060,
  17061 (amount 20..100); 17057 (bear rage) and 17099 (cat energy) are the proc spells, not ranks.
- `FeralFormulas`: attack power by form with Predatory Strikes (`StatSystem.cpp:194-296`), ranged AP, feral damage
  range (`StatSystem.cpp:354-435`), attack time 1000 / 2500 (`Player.cpp:18271-18312`).
- `RipDamageRules`: per-tick term `AP * min(cp, 4) / 100`, added after the combo scaling (`SpellAuras.cpp:4341-4363`,
  4420-4438).
- `FrenziedRegenerationRules`: up to 100 stored rage per tick converted at amount/10 (`SpellAuras.cpp:1417-1433`).
- `ShapeshiftFormEffectRules`: which roots and snares spell 9033 removes (`SpellEffects.cpp:4442-4480`,
  `Unit.cpp:543-556`, `SpellDefines.h:710-714`).

### High-liquid aura and channel interrupts
`LiquidAuraInterruptUpdater` (an `IMapUpdater`, attached to every map by `LiquidInterruptFeature`) replays
vmangos `ENVIRONMENT_FLAG_HIGH_LIQUID` (`Player.cpp:20353-20419`, `Player.cpp:848-855`): when a player moved, the
`ILiquidProbe` (default `TerrainLiquidProbe`: liquid status at `z + 0.01`, in-water or under-water, surface above
`z + 0.75 * 2.0`) is asked; on a change entering deep liquid removes auras and stops channels with
`AURA_INTERRUPT_UNDER_WATER_CANCELS` (0x80, Travel Form, mounts, Food, Drink), leaving removes those with
`ABOVE_WATER_CANCELS` (0x100, Aquatic Form). It is edge-triggered; the state before the first probe is "not in deep liquid" (vmangos m_environmentFlags starts at 0), so a player first probed in deep water gets the entering edge. Also
`HighLiquidChanged` is raised for other systems (breath/fatigue timers belong to world-state-exploration).
`AuraInterruptMasks`, `RemoveAurasWithInterruptFlags` and `InterruptChannelWithFlags` are reusable helpers built from
the public `SpellSystem` API.

### Taxi interlock
`FormInterlocks.BlocksTaxi` and a 7-line additive edit in `QuestNpcServices.Travel.cs`: a flight-master request in
a disallowed form answers `ERR_TAXIPLAYERSHAPESHIFTED` (9) before anything is charged (`Player.cpp:17872-17880`).
Merge point with the npc-services-quests lane.

## Not delivered (needs another lane's primitive or data we do not have)

- **ModShapeshift (aura 36) and everything built on it**: form byte, display, power switch on shift, Furor roll,
  linked boosts, 9033 cast, passive recast, stance and mount cast gates, Moonkin behaviour, feral stat wiring.
  The design assigns aura 36 to the warrior-mechanics ShapeshiftService (S06) with a druid behaviour hook; that
  service, `SpellInfo.Stances/StancesNot`, `ISpellCastCheck` and the `spell_shapeshift_form` table do not exist in
  this tree. `RegisterAura` replaces silently, so a second registration would be a latent bug.
- Cat/bear abilities (combo points, Rip snapshot, Pounce/Ravage stealth, behind rule, Maul next-swing, taunts,
  Frenzied Regeneration script): need the shared combo-point API, stealth, next-swing and spell-script primitives.
- Nature spells (Innervate, Rebirth, Hurricane, Thorns, Hibernate, Barkskin ...): need regen modifiers,
  resurrection requests, persistent area auras, damage shields, creature-type checks.
- Cast-start `ACTION_CANCELS` / `ACTION_CANCELS_LATE` removal (`Spell.cpp:3445-3456, 3700-3715`; needed by Prowl):
  a call-site edit in `SpellSystem.cs` owned by the spell lanes. Prowl/Pounce/Ravage openers stay undone.
- Item-use and weapon-skill-gain interlocks: need `SpellShapeshiftForm.dbc` flags1 (`IsShapeShifted`).
- `Druid:*` configuration (`RequireFormDbc`, `BehindOnlyExemptions`): has no consumer until the form engine exists,
  so it is not added (no dead options).

## Limits of what is delivered

- Liquid probe: terrain liquid only (no WMO liquid), default collision height 2.0 instead of model data, object scale
  not applied (every player has scale 1.0 today, so Tauren native scale cannot be restored either).
- `IsDisallowedMountForm` covers the form-id half only; the display half (non-native display whose model cannot mount)
  needs the CreatureDisplayInfo DBCs.
- Feral damage range uses identity modifiers (base_pct, total_pct); the stat system owns those layers.
- Frenzied Regeneration returns the exact float; the source dithers it to an integer.
- No weapon-skill formula: the "max for level" rule depends on flags1 and the skills lane.

## Facts established while verifying (corrections to earlier assumptions)

- Moonkin Form is present in 1.12.1 data (classic-db spells 24858, 24905, 24907; `FORM_MOONKIN` 0x1F in both
  reference cores, display 15374/15375). Tree of Life (775, 5420) exists in data but no trainer teaches it. How a
  character obtains Moonkin (talent) is unverified: `Talent.dbc` is not in the references.
- Predatory Strikes (16972/16974/16975) is a plain Dummy passive with Stances 0; vmangos reads it by SpellIconID 1563
  only while in cat/bear/dire bear. It is not recast per form.
- vmangos shapeshift removes the previous form first, which resets a druid to mana and rage 0, so rage is never
  "retained" across a shift. Rage is non-zero after login only if the vitals restore runs after the aura re-apply.
- DB amounts are `EffectBasePoints + 1` for die sides 0 or 1: Cower -240, Demoralizing Roar -30, Cat passive threat -29,
  Dire Bear passive attack power 120.
- vmangos and mangos-classic disagree on druid attack power (mangos-classic `StatSystem.cpp:243-258` uses
  `level*3 + 2*str - 20` and agility by form flag), form scale and Furor handling. vmangos is followed; no
  switch to the mangos-classic values is provided.
- The spell cast gates `ONLY_ABOVEWATER` / `ONLY_UNDERWATER` use the client swim flag first (`Spell.cpp:5355-5372`),
  not the terrain probe; the probe only drives the aura-interrupt edge.

## Integration requests (for the integrator)

1. warrior-mechanics S06: expose a per-form behaviour hook (display/scale, Furor, boosts, 9033). It is a proposed
   contract, not an existing seam. Until then forms stay inert buffs.
2. Exactly one owner for aura 36; add a guard test that fails if two modules register it.
3. Combo points: one shared API (warrior S09, spell-breadth S5 or class-rogue); the Rip term must be read before
   the finisher clears the points.
4. death-persistence life-persistence must restore all five power fields after `RestoreAuras`.
5. `PowerTypeSwitch.EnsureFeralPowerCaps` is now called from the `Player` constructor; a stats lane that rewrites max powers must not lower rage/energy maxima.

## Provenance

Constants and formulas: `D:\refs\vmangos\src\game\` files named in each source comment. Spell ids and base points
were checked in the classic-db 1.12.1 `spell_template` dump read outside the repository (not committed). Test spell
ids in comments (768, 783, 1066, 1079, 16975, 17056..17061, 22842, 22896, 3025, 24932) are cited from that dump.
No code or data was copied from the GPL references.
