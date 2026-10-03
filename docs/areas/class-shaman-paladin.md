# Area: Shaman and paladin mechanics (totems)

Branch `claude/vw2-class-shaman-paladin`, built on `49448fd`. This lane delivers the **shaman totem
system** and the spell-core primitives it needs. Paladin content (seals, judgement, blessings, auras, bubbles)
and most shaman imbue content are **not** delivered; every reason is listed under "Not delivered".

References (read only, never copied): **vmangos** `D:\refs\vmangos` (primary), **mangos-classic**
`D:\refs\mangos-classic`, **wow_messages** `D:\refs\wow_messages`, **classic-db**
`D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz`. Every formula cites file:line inline in the code.

## Delivered

### 1. Implicit target selector registry (`Spells/Targets/SpellTargetSelectors.cs`, `SpellSystem.TargetSelectors.cs`)
- `SpellSystem.RegisterTargetSelector(target, handler, locationOnly)`; a duplicate registration throws.
  One additive hook in `SpellSystem.Targeting.cs` (`default:` branch and `IsLocationTarget`).
- Registered here: 41 FRONT_RIGHT (+1.75 pi), 42 BACK_RIGHT (+1.25 pi), 43 BACK_LEFT (+0.75 pi),
  44 FRONT_LEFT (+0.25 pi), 47 FRONT (+0) after vmangos `Spell.cpp:2977-3022`: unless the client sent a
  destination, the destination is the effect radius from the caster at orientation + angle (0 when the
  effect has no radius index); the unit list falls back to the caster.
- 61 `TARGET_UNIT_RAID_AND_CLASS` after `Spell.cpp:2940-2960`: a grouped player target hits every member of
  its whole group of the same class within the effect radius that the caster is not hostile to, else just
  the explicit target. All 67 castable shaman totem summon ids use 41-44 and all 8 Greater Blessings use 61.
- **Limit:** vmangos uses `GetFirstCollisionPosition` (wall push-back, ground snap); the destination here is the
  unclamped offset at the caster's Z.

### 2. `totem_spell` data (`Kernel/WorldData/Totems`, `Data/World/Totems`)
- classic-db has no totem spell column; mangos-classic takes the first spell of the creature's list
  (`Entities/Totem.cpp:171-178`). `TotemSpellDumpImporter` resolves it by column name from
  `creature_template_spells.spell1` (set 0), else position 0 of `creature_spell_list`, for every creature named
  by a SUMMON_TOTEM / SLOT1-4 effect (`spell_template.EffectMiscValueN`) or with `AIName = TotemAI`.
- Measured on `ClassicDB_1_12_1_z2815` (the env-gated test `RealClassicDb_TotemSpellCounts_AreMeasured_AndSentryHasNone`):
  95 totem creatures get a spell (none needs the list fallback) and 8 have none, Sentry Totem 3968 among them.
- **Schema constant:** `TotemWorldDataModule.Version = 11` (World; table `totem_spell`). The integrator renumbers;
  tests use the constant. No Characters schema change (totems are never persisted: vmangos unsummons them on logout).
- `ARCANECORE_CLASSIC_DB` (path to the dump, `.sql` or `.sql.gz`) enables the real-data audit; unset it is reported
  Skipped, never green.

### 3. Totem system (`Game/Totems`, `World/Spells/Totems/TotemFeature.cs`)
- Effects 74 SUMMON_TOTEM (no slot), 87-90 SUMMON_TOTEM_SLOT1-4 (fire, earth, water, air), 110 DESTROY_ALL_TOTEMS
  after vmangos `SpellEffects.cpp:4923-5003` and `:5566-5573`: the old totem in the slot is unsummoned first, the
  creature is placed 2.0 yd from the caster at angle `pi/4 - slot * pi/2`, takes the owner's faction and level, the
  effect value as health (5 for 61 of 67 ids), `UNIT_FIELD_SUMMONEDBY` / `UNIT_FIELD_CREATEDBY` /
  `UNIT_CREATED_BY_SPELL`, `PLAYER_CONTROLLED` for player owners and the owner's PvP flag.
- A totem is an ordinary `Creature` (sealed class) recorded in `TotemQuery` (`IsTotem`, `TryGet`, `GetOwnerGuid`),
  built from a copy of its template with `AIName = NullAI` and idle movement, so the unknown `TotemAI` name never
  falls back to AggressorAI and nothing wanders or aggroes.
- Passive totem spell (`Totem::Summon`, `Totem.cpp:94-112`): cast on itself, triggered, at summon; a spell with a cast
  time marks an active totem and is not cast here. The periodic timer of the totem passives 8145, 6474, 8179, 8172,
  8167, 8515, 10609, 10612 and of Stoneclaw (`SpellVisual 0`, icon 689) starts at 0 (first tick on the next update),
  `SpellAuras.cpp:8053-8112`.
- Lifecycle (`Totem::Update`, `Totem.cpp:66-92`) every map update: unsummon when the owner left the world, a
  player/pet owner is dead (a creature owner's death does not unsummon), the totem is dead, the owner is beyond
  `Map.IsWithinVisibilityDistance` (option `OwnerLeash`) or the summon spell's duration ran out. An owner leaving the
  map (logout, far teleport) unsummons all (`Player::RemoveFromWorld`, `Player.cpp:2208-2216`).
- Unsummon (`Totem::UnSummon`, `Totem.cpp:114-150`): SMSG_GAMEOBJECT_DESPAWN_ANIM, the totem's aura removed from the
  totem, the owner and the owner's sub-group, slot freed, creature removed. SMSG_GAMEOBJECT_SPAWN_ANIM is sent one
  map update after the summon so observers already know the creature (the creature block goes out in the visibility
  phase). No totem bar packet exists in 1.12.1 (SMSG_TOTEM_CREATED / CMSG_TOTEM_DESTROYED are 2.4.3+), none is sent.
- `OwnerAwareGroupResolver` decorates `SpellSystem.Groups`: a totem resolves its owner's party (vmangos
  `SpellAuras.cpp:597-640`), so a totem's party area aura reaches the shaman's sub-group.
- `TotemFeature` attaches after `SpellFeature` and `CreatureWorldFeature` (full type name order), loads `totem_spell`,
  installs the effects, wraps the group resolver and attaches `TotemMapUpdater` to every map.
- **Config (section `Totems`, every default is retail):** `Enabled=true` (false leaves the effects unregistered, they
  report "not implemented"), `PlacementDistance=2.0`, `OwnerLeash=true`.
- `TotemSystem.Register` throws when any of its effects already has a handler, because `RegisterEffect` replaces by
  key and two lanes registering the same effect would silently clobber each other.

## Limits (retail behaviour not reproduced)
- No collision/ground-pushed totem placement (see 1).
- vmangos runs the totem's own `Creature::Update` once more before unsummoning so its last aura tick is not lost;
  here auras tick inside the spell system's own update, so the last tick can land up to one tick either side of expiry.
- An unsummoned totem is removed at once; vmangos first sets it dead for the client animation.
- The per-effect totem immunity rule (`Totem.cpp:180-216`: immune to heal, energize, negative auras and regeneration
  auras except the Healing Stream / Mana Spring / Mana Tide family mask `0x4006000`) needs an immunity seam in the spell
  core that does not exist; it is **not applied**. A killed totem still fires `MapCombat.UnitKilled`, so kill XP and
  quest credit consumers must call `TotemQuery.IsTotem` (vmangos `Player::IsHonorOrXPTarget` excludes totems).
- The totem area aura still lands on the totem itself (vmangos sets its aura name to NONE,
  `SpellAuras.cpp:424-436`); friendliness/PvP filters, tick synchronisation and per-member rank selection of the area
  aura are not ported (`SpellSystem.AreaAuras.cs` belongs to the spell-breadth lane).
- Active (cast-time) totems, i.e. Searing Totem (6 ids), do not cast: no TotemAI port (`AI/TotemAI.cpp:66-111`).

## Not delivered (slices not done)
Each needs a primitive another lane owns, data this tree does not have, or was not reached.
- `totem-active-casting` (Searing Totem), `totem-grounding-magnet`, `totem-immunity`, `area-aura-fidelity`:
  not reached this run; the first needs the creature-ai TotemAI decision, the third a spell immunity seam, the
  fourth edits `SpellSystem.AreaAuras.cs` (spell-breadth S2).
- `weapon-imbue-core`, `totem-held-item-enchant`, `rockbiter-weapon-damage`, `imbue-combat-procs`: not reached; imbues
  need SpellItemEnchantment.dbc import, a temp-enchant lifecycle and the items lane's enchant plumbing.
- Everything gated on the proc engine, script registry (`ISpellScript`), `ISpellCastCheck`, shapeshift service, aura-state
  service, immunity ledger or death lane: seals, Judgement, Holy Shock, Hammer of Wrath, blessings and Greater
  Blessings (target 61 now exists), paladin auras and Concentration, bubbles with Forbearance, Lay on Hands, Divine
  Intervention, creature-type targeting, Ghost Wolf, Lightning Shield, Reincarnation, Water Walking.
- Consecration (persistent area aura), Redemption/Ancestral Spirit (resurrect effects), Far Sight, Sentry Totem camera,
  Water Breathing: other lanes or no camera/breath system.

## Tests
- `tests/ArcaneCore.Game.Tests/ClassSpells`: `LocationTargetTests` (13), `ShockCooldownGuardTests` (a labelled GUARD,
  green on arrival; mutating a rank's category turns it red), `Totems/TotemSummonTests` and `TotemLifecycleTests` (23).
- `tests/ArcaneCore.Data.Tests/Totems/TotemSpellDataTests` (importer, schema step, EF round trip, real-data audit).
- `tests/ArcaneCore.World.Tests/ClassSpells/Totems/TotemLoopbackTests`: the real host end to end.
- Real-client checklist (not verifiable here): totem model and spawn animation, totem placement relative to the
  character, party buff icons from totem area auras, no totem bar.
