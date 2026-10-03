# Integration notes: spell persistence, targeting, effects and combat rules

Area: fleet round 2 follow-up to [spells.md](spells.md) (branch `feat/spells-persistence`,
branched from `codex/integrate-feature-fleet-20261003` at `0d32fba`, then merged with the
integration head that carries `feat/character-delete-cleanup` (#21), `feat/vmap-los` (#17) and `feat/reputation` (#19)). Behaviour is re-implemented from
vmangos / cMaNGOS-classic (`Spell::FillTargetMap`/`SetTargetMap`, `Spell::EffectWeaponDmg`,
`EffectHealthLeech`, `EffectDispel`, `EffectInterruptCast`, `EffectApplyAreaAura`,
`AreaAura::Update`, `Unit::SpellHitResult`, `SpellCaster::MagicSpellHitChance`, `IsSpellCrit`,
`Unit::CalculateAbsorbResistBlock`, `Spell::Delayed`/`DelayedChannel`, `Player::_SaveAuras`/
`_LoadAuras`/`_SaveSpellCooldowns`) with gtker/wow_messages (MIT) as the wire cross-check. No
GPL code was copied; each member cites its source behaviour.

## Schema

| Component | Version | Module | Tables |
|---|---|---|---|
| characters | **8** | `ArcaneCore.Data.Characters.Spells.CharacterSpellStateDataModule` | `character_spell_cooldown`, `character_aura` |

Characters v8 was reserved for this work and follows reputation's v7 (#19), which is now on the
base. The version is the single constant `CharacterSpellStateDataModule.Version`;
`IntegratedSchemaTests` expects characters `8` / `[2..8]`.

- `character_spell_cooldown` (CharacterId, Kind, Id, EndsAtUnixMs), key (CharacterId, Kind, Id).
  Kind 0 = spell cooldown, 1 = Spell.dbc category cooldown. Ends are absolute Unix ms, so
  cooldowns keep running offline (vmangos `character_spell_cooldown`).
- `character_aura` (CharacterId, Seq, Spell, CasterGuid, CasterLevel, StackCount, Charges,
  MaxDurationMs, RemainingMs, EffectMask, Amount0-2, PeriodicTimer0-2, SavedAtUnixMs), key
  (CharacterId, Seq). Mirrors vmangos/cMaNGOS `character_aura` (basepoints, periodictime,
  remaincharges, effIndexMask). `CasterGuid` is `ulong` (MariaDB `bigint unsigned`, PostgreSQL
  `numeric(20,0)`, SQLite integer); the provider matrix test stores a GUID above 2^63.

## Shared files edited

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.Game/Combat/MapCombat.Melee.cs` | One line in `DealDamage`: `DamageDealt?.Invoke(attacker, victim, damage, direct, meleeDamage)` after non-lethal damage is applied. | Weapon hits must push back/interrupt casts and break damage-interruptible auras. The event itself is declared in a new partial file, `MapCombat.DamageEvents.cs`; nothing else in combat changes. |
| `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` | Adds the new module to the expected list after reputation; characters current version 7 → 8, steps `[2..7]` → `[2..8]`. | The test hardcodes the integrated allocation. |
| `src/ArcaneCore.Game/Spells/SpellSystem.cs` `CheckCast` | The vmap-los hunks (#17: `SpellLineOfSight.Check`/`CheckDest`) are kept as merged; this branch only replaces the final `return CheckPower(...)` with `CheckTargetRules` (party/raid targets, nothing to dispel) followed by `CheckPower`, and extends the doc comment. | Keeps the merge with #17 conflict-free; LOS for explicit targets stays owned by vmap-los. |
| `src/ArcaneCore.World/Spells/SpellCharacterDeleteHook.cs` (from #21) | `OnCharacterDeletingAsync` also waits for this character's queued state saves; `OnCharacterDeletedAsync` also calls `SpellStatePersistence.DeleteCharacter`. | Deleted characters must not leave an unsaved snapshot or rows for a reused id. |

Files in this area's own folders (spells): `SpellSystem*.cs`, `SpellAuraHolder.cs`, `SpellCast.cs`,
`SpellDefines.cs`, `SpellInfo.cs`, `SpellPackets.cs`, `SpellStoreFactory.cs` (maps `ProcCharges`
and `DmgMultiplier1-3`), `SpellFeature.cs`, the spell test kits and `SpellTestServices`
(registers an in-memory `ICharacterSpellStateStore` and two extra synthetic spells).

## What is implemented

### Targeting (`SpellSystem.Targeting.cs`)
- Per-effect target maps with a per-target effect mask and per-effect value multiplier. When
  target A is location-only (e.g. `LocationCasterSrc` for Arcane Explosion), target B picks units.
- Caster, explicit unit, random unit near caster (`UnitEnemy/FriendNearCaster`, count =
  EffectChainTarget), areas around the caster (src, within caster range), around the destination
  (client dest, else explicit target, else caster), frontal cone (π/2 arc, see limits), party and
  raid areas, friend-and-party, single party/raid targets (`UnitParty`/`UnitRaid`, CheckCast
  `BadTargets` for non-members).
- Chains: up to EffectChainTarget units, each within 10 yd of the previous one and in its line of
  sight; damage chains take the nearest enemy, chain heals the most injured friend; each jump
  multiplies the value by Spell.dbc `DmgMultiplier`.
- Area units: 3D distance plus the unit's bounding radius; alive, relation filter
  (`ISpellTargetRelations`, default = the map's `CombatHooks`), line of sight from the area
  centre, `CANT_TARGET_SELF` honoured, `MaxAffectedTargets` keeps a random subset.
- Line of sight: the vmap-los seam ([vmap-los.md](vmap-los.md)). Area and chain targets use
  `map.Collision` (`IsWithinLineOfSight` unit to unit, `IsInLineOfSight` from the area centre,
  both ends at `MapCollision.DefaultEyeHeight`); without collision data everything is visible.
  Explicit units and destinations are checked in CheckCast by vmap-los's `SpellLineOfSight`
  (non-triggered casts only, as vmangos). `SPELL_ATTR_EX2_IGNORE_LINE_OF_SIGHT` skips both. The
  earlier spells-owned `ILineOfSight` seam was removed in favour of
  `ArcaneCore.Game.Maps.Collision.ILineOfSight`; tests install a fake through
  `WorldCollision.Of(world).Install(...)`.
- Groups: `ISpellGroupResolver`; the world daemon adapts the social area's `GroupManager`
  (`WorldSpellGroups`: party = the raid sub-group when in a raid).

### Effects
- `WEAPON_DAMAGE`, `WEAPON_DAMAGE_NOSCHOOL`, `NORMALIZED_WEAPON_DMG`, `WEAPON_PERCENT_DAMAGE`:
  combined once per target (flat bonuses added to the weapon roll, then the percent); normalized
  rolls replace the attack-power part at the weapon speed with the normalized speed
  (`NormalizedWeaponSpeed`, default 2.4 one-hand / 2.8 ranged); armor, crit ×2.
- `HEALTH_LEECH`: damage, then the caster heals dealt × EffectMultipleValue.
- `ENVIRONMENTAL_DAMAGE`: school damage that never crits.
- `DISPEL`: up to the effect value random auras of the dispel type — harmful from friends,
  beneficial from enemies; dispel-only spells fail CheckCast with `NothingToDispel`.
- `INTERRUPT_CAST`: a cast with a cast bar or a channel whose PreventionType is SILENCE is
  cancelled and the school is locked for the spell duration (`IsSpellReady` → `NotReady`); the
  victim gets SMSG_SPELL_COOLDOWN for the interrupted spell.
- `SUMMON`: through `ISpellSummonSink` at the destination (or the caster) for the spell duration.
- `APPLY_AREA_AURA_PARTY`: the caster holds the source aura; each spell update puts a child
  holder (same caster ownership token, remaining duration, area effects only) on alive party
  members of the same map within the effect radius and removes children from members that left
  the party/radius/map, when the source is removed, or when the caster logs out.

### Combat rules (`ISpellCombatRules`)
- `SpellCombatRules.Neutral` (Game default: always hit, no crit/resist/armor) keeps unit tests
  deterministic; the world daemon uses `VanillaSpellCombatRules` unless an `ISpellCombatRules`
  service is registered.
- Hit: damage class NONE and positive spells always land; magic = 96 − level diff (diff < 3),
  else 94 − 7 (player) / 11 (creature) per level beyond 2, plus `MOD_SPELL_HIT_CHANCE`, clamped
  1–99 (miss = SPELL_MISS_RESIST); melee/ranged spells roll miss/dodge/parry from the combat
  area's roll input. A miss sends SMSG_SPELLLOGMISS, lists the target as missed in SMSG_SPELL_GO
  and deals 0 damage to a hostile target so combat starts.
- Crit: magic = 5% player base + `MOD_SPELL_CRIT_CHANCE(_SCHOOL)`, ×1.5; melee/ranged =
  `PLAYER_(RANGED_)CRIT_PERCENTAGE` (creatures 5%) + `MOD_CRIT_PERCENT`, ×2; `CANT_CRIT` and
  damage class NONE never crit. Heals can crit. The damage log carries hitInfo 0x2.
- Partial resist (magic, non-holy, non-binary spells, direct and periodic): average fraction =
  resistance × 0.15 / caster level (cap 75%), creatures above the caster +8 resistance per level;
  the result is one of the two 25% steps around the average. Logged in the damage/periodic log.
- Armor for school NORMAL spell damage through the combat area's `MeleeHitTable.ApplyArmor`.

### Pushback and interrupts (`OnDamageTaken`)
- Direct damage (spells and, through `MapCombat.DamageDealt`, weapon hits) on a caster:
  `DamageCancels` interrupts, `DamagePushback` adds 500 ms capped at the cast time
  (SMSG_SPELL_DELAYED). DoTs never push back casts.
- Channels: `CHANNEL_FLAG_DELAY` shortens the channel and its auras by 25% of the duration
  (MSG_CHANNEL_UPDATE); `CHANNEL_FLAG_DAMAGE(2)` interrupts.
- Auras with `AURA_INTERRUPT_FLAG_DAMAGE` (and `NON_PERIODIC_DAMAGE` for direct hits) break.

### Persistence (`SpellSystem.Persistence.cs`, `SpellStatePersistence`, `SpellFeature`)
- Logout (`PlayerLoggingOut`, world thread): `CaptureState` before the unit leaves the spell
  system — running spell/category cooldowns (absolute ends) and saveable auras (not passive, not
  channeled, not area children). The save is queued per character off the world thread.
- Login (`OnPlayerLoadingAsync`, session task): waits for that character's queued saves, loads
  (fails the login on storage errors), and stages the snapshot. Cooldowns are restored in
  `BuildInitialSpells` so SMSG_INITIAL_SPELLS lists them; auras are restored in `PlayerLoggedIn`.
- Harmful auras lose the offline time; beneficial auras keep their remaining time; permanent
  stays permanent. Stacks are clamped to the spell's StackAmount; charges, per-effect amounts
  and periodic timers are restored; unknown/passive/channeled spells and effects that are no
  longer auras are skipped.
- **Ownership on reload** ([aura-caster-ownership.md](aura-caster-ownership.md)): an aura the
  player cast on itself gets the player's fresh token (attribution and stacking continue). An
  aura from anyone else keeps its caster GUID/level as provenance only and gets a permanently
  revoked token: periodic effects use the target fallback even if a unit with that GUID is
  online, and that caster's next cast replaces it instead of stacking.
- A failed save is logged and the snapshot stays in memory: the next login in this process
  restores it and the next logout retries the write. Without any store registered the state
  persists in memory only. `StopAsync` waits for queued saves.
- **Character deletion** ([character-delete.md](character-delete.md)): the module implements
  `ICharacterDataCleanup` (both tables by owner, inside the deletion transaction; auras the
  deleted character cast on others stay with their caster GUID as provenance). The spells delete
  hook drains the character's queued saves before the rows go and afterwards drops any unsaved
  in-memory snapshot (`SpellStatePersistence.DeleteCharacter`, whose ordered store delete is a
  no-op by then).

## Tests

- Game (`tests/ArcaneCore.Game.Tests/Spells/`): `SpellTargetingTests` (12), `SpellEffectCombatTests`
  (22 cases incl. theory rows), `SpellPersistenceTests` (7) — areas, cone, cap, chains, LOS,
  party/raid, weapon/leech/environmental/dispel/interrupt/summon/area aura, miss/crit/resist/armor,
  vanilla formulas and a statistical hit check, pushback/channel delay/interrupt, the combat
  damage event, capture/restore and the ownership-on-reload regression.
- Data (`tests/ArcaneCore.Data.Tests/Spells/CharacterSpellStateStoreTests.cs`): module/step shape,
  a SQLite/MariaDB/PostgreSQL round trip (replace, isolation, delete, GUID > 2^63, -1) and the
  character-deletion cleanup removing only the deleted character's rows; the shared
  `CharacterDeletionTests` guard covers the module too.
- World (`tests/ArcaneCore.World.Tests/Spells/SpellPersistenceWorldTests.cs`): real relog
  restores cooldowns in SMSG_INITIAL_SPELLS and a self aura with ownership; a stored foreign aura
  stays orphaned while its caster is online; a failed logout save is restored from memory and
  written on the next logout; a melee hit pushes back a cast through `MapCombat.DamageDealt`;
  CMSG_CHAR_DELETE removes both the stored rows and an unsaved in-memory snapshot.

## Known limits

- Cone arc is π/2 (`SpellConstants.ConeArc`); vmangos's per-spell cone angle was not confirmed.
- SMSG_SPELL_DELAYED is written with a packed caster GUID (vmangos); gtker lists a full GUID for
  1.12 — not verified against a client.
- No SMSG_SPELLDISPELLOG and no dispel resistance; the interrupt's SMSG_SPELL_COOLDOWN names only
  the interrupted spell, not every spell of the school.
- Summon is a seam only: no creature/pet/totem summoning exists yet (without a sink the effect is
  reported as not implemented).
- Player base spell crit is a flat 5% until the stats area provides intellect-based crit; the
  resist distribution is the simplified two-step spread; binary spells get no extra resist chance
  (they are only excluded from partial resists); no spell penetration or hit-from-gear.
- Saves happen on logout only: no periodic autosave or crash-safe save of auras/cooldowns.
- Line of sight covers static models only (vmap-los limits: no terrain or game-object LOS).
- Proc charges are persisted and restored but nothing consumes them yet (no proc system).
- Melee damage forwarding needs the combat area's `MapCombat`; units that take damage outside it
  call `SpellSystem.OnDamageTaken` themselves.

## Suggested next slice

Proc system (`PROC_FLAG_*`, charges consumption, `SPELL_AURA_PROC_TRIGGER_SPELL`), stat-driven
spell crit/hit, totems and summoned creatures through `ISpellSummonSink`, and an autosave hook so
auras/cooldowns survive a crash.
