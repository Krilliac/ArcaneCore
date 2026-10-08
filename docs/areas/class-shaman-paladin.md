# Area: Shaman and paladin mechanics (totems)

Branch `claude/vw2-class-shaman-paladin`, built on `49448fd`. This lane delivers the **shaman totem
system** and the spell-core primitives it needs. Paladin content (seals, judgement, blessings, auras, bubbles)
and most shaman imbue content were **not** delivered by this lane; the wave-2 class-scripts lane delivered seals, Judgement, the judgement procs,
blessing/aura/seal stacking, Holy Light and Flash of Light, Blessing of Light, the resistance auras, Forbearance, Holy Shock, Hammer of Wrath,
Consecration (persistent area auras) and the Flametongue/Rockbiter imbue procs: see [class-scripts](class-scripts.md).

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
- classic-db has no direct totem spell column; cmangos takes the first spell of the creature's list.
  `TotemSpellDumpImporter` resolves an explicit `SpellList` to its lowest-position positive spell;
  otherwise it uses the first nonzero `spell1..spell10` of default-set `creature_template_spells`.
  Candidates are creatures named by SUMMON_TOTEM / SLOT1-4 effects or with `AIName = TotemAI`.
  vmangos instead supplies `creature_template.totem_spell_id` directly, including zero (no fallback).
  See [content import](content-import.md) for pinned references, supported patch selection and limits.
- Measured on `ClassicDB_1_12_1_z2815` (the env-gated test `RealClassicDb_TotemSpellCounts_AreMeasured_AndSentryHasNone`):
  95 totem creatures get a spell (none needs the list fallback) and 8 have none, Sentry Totem 3968 among them.
- **Schema constant:** `TotemWorldDataModule.Version = 15` (World; table `totem_spell`; built as 11, renumbered by the wave-2 integrator);
  tests use the constant. No Characters schema change (totems are never persisted: vmangos unsummons them on logout).
- **The `totem_spell` table stays empty until the import is run.** The production
  `ArcaneCore.ContentImporter import` command now fills it inside the shared content transaction;
  plan, dry-run, JSON reports and verify include its mappings. With an empty table totems have neither
  passive auras nor active spells, and `TotemFeature.Attach` logs a warning naming the import command.
- `ARCANECORE_CLASSIC_DB` (path to the dump, `.sql` or `.sql.gz`) enables the real-data audit; unset it is reported
  Skipped, never green.

### 3. Totem system (`Game/Totems`, `World/Spells/Totems/TotemFeature.cs`)
- Effects 74 SUMMON_TOTEM (no slot), 87-90 SUMMON_TOTEM_SLOT1-4 (fire, earth, water, air), 110 DESTROY_ALL_TOTEMS
  after vmangos `SpellEffects.cpp:4923-5003` and `:5566-5573`: the old totem in the slot is unsummoned first, the
  creature is placed at angle `pi/4 - slot * pi/2` and `PlacementDistance` (2.0) plus the caster's and the totem's
  bounding radii from the caster (`Creature.cpp:232`, `Object.cpp:2728-2729` and `:2748`), takes the owner's faction and level, the
  effect value as health (5 for 61 of 67 ids), `UNIT_FIELD_SUMMONEDBY` / `UNIT_FIELD_CREATEDBY` /
  `UNIT_CREATED_BY_SPELL`, `PLAYER_CONTROLLED` for player owners and the owner's PvP flag.
- A totem is an ordinary `Creature` (sealed class) recorded in `TotemQuery` (`IsTotem`, `TryGet`, `GetOwnerGuid`),
  built from a copy of its template with `AIName = NullAI` and idle movement, so the unknown `TotemAI` name never
  falls back to AggressorAI and nothing wanders or aggroes.
- Passive totem spell (`Totem::Summon`, `Totem.cpp:94-112`): cast on itself, triggered, at summon; a spell with a cast
  time marks an active totem, whose normal casts are driven by the map updater. The periodic timer of the totem passives 8145, 6474, 8179, 8172,
  8167, 8515, 10609, 10612 and of Stoneclaw (`SpellVisual 0`, icon 689) starts at 0 (first tick on the next update),
  `SpellAuras.cpp:8053-8112`.
- Active totems (`TotemSystem.Active.cs`, vmangos `AI/TotemAI.cpp:66-125` at
  `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`): retain their spell victim while valid; otherwise use the owner's
  current combat victim or first attacker, then the nearest hostile unit inside the spell range plus both bounding radii
  (`Unit.h:1066-1074`, `Maps/GridNotifiers.h:880-898`). Selection checks the target's life, map and attackable flags,
  the owner's attack permission and PvP gate; spontaneous acquisition must not enable the owner's PvP
  (`Unit.cpp:9985-9992`). Stealthed targets are rejected with `detect=false`, including at point-blank range;
  the totem's own stalk aura remains the earlier visibility exception (`Unit.cpp:6388-6452`).
  Ordinary `CastSpell(..., triggered:false)` keeps cast time, power, cooldown and LOS checks. No second cast is
  prepared while a generic cast or channel is running; no melee victim, threat chase or movement is started.
- The totem is rooted using the existing server movement flag. A cast check rechecks owner life/world/leash at
  preparation and landing, because the global spell update can precede the map lifecycle update. This prevents a
  bolt from landing after a player owner's death even when the next world tick spans the whole cast time.
  Unsummon drops the totem's live spell state immediately. Expiry remains a map lifecycle decision and may permit
  the reference's last update before removal (`Totem.cpp:66-92`).
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

### 4. Intrinsic immunity and Grounding protection

- `TotemImmunity` applies the source's per-effect exclusions for foreign healing, energize,
  taunts, negative auras and periodic regeneration. Self casts and the Shaman regeneration
  family mask `0x4006000` bypass these checks. Damage in a mixed spell still lands;
  a spell with every effect excluded reports an immune miss. See
  [totem immunity evidence](../integration/totem-immunity-20261004.md).
- `SPELL_AURA_SPELL_MAGNET` (96) redirects eligible hostile magic casts to the live aura caster,
  spends one protection charge per cast, and updates the spell-go target and outcome.
  Consuming the last charge removes the source protection and party children. Normal damage
  can kill the totem; a miss or non-damaging cast consumes protection without creating a death.
  Redirected channels clean up their actual target when interrupted or when that target disappears.
  See [Grounding evidence and eligibility limits](../integration/grounding-totem-20261004.md).

Ordinary resurrection effects used by Redemption/Ancestral Spirit now offer and accept player
resurrection through the existing teleport handshake; real spell data and client acceptance remain pending.
See [player resurrection](../integration/player-resurrection-20261004.md).

## Limits (retail behaviour not reproduced)
- No collision/ground-pushed totem placement (see 1).
- vmangos runs the totem's own `Creature::Update` once more before unsummoning so its last aura tick is not lost;
  here auras tick inside the spell system's own update, so the last tick can land up to one tick either side of expiry.
- An unsummoned totem is removed at once; vmangos first sets it dead for the client animation.
- A killed totem still fires `MapCombat.UnitKilled`, but `KillRewards.AwardExperience` returns no XP for it
  (`Player::IsHonorOrXPTarget`, `Player.cpp:19943-19954`; `MaNGOS::XP::Gain`, `Formulas.h:102-107`) and
  `QuestObjectiveAdapter` gives no kill credit for a player-owned totem (a player-owned victim is PvP in
  `RewardSinglePlayerAtKill`, `Player.cpp:19961-19982`). A creature-owned totem still credits the kill, as in vmangos.
  Honor is not modelled by this tree. The server root flag and NullAI keep totems stationary; forced movement effects
  that ignore root remain the responsibility of the movement/spell core.
- The totem area aura still lands on the totem itself (vmangos sets its aura name to NONE,
  `SpellAuras.cpp:424-436`); friendliness/PvP filters, tick synchronisation and per-member rank selection of the area
  aura are not ported (`SpellSystem.AreaAuras.cs` belongs to the spell-breadth lane).
- Active targeting inherits the combat core's faction/reputation and controlling-player limitations. Ordinary
  `Creature` does not expose `IPlayerControlledUnit`, so owner-linked pet/creature PvP and same-faction duel reaction
  parity are not claimed. Stealth uses the installed registry; separate invisibility masks are not implemented.
- Retail selection does not prefilter ordinary visible units by LOS: a nearest enemy behind a wall can remain selected
  and prevent casts while a farther enemy is reachable. Normal spell casting refuses the blocked bolt.

## Not delivered (slices not done)
Each needs a primitive another lane owns, data this tree does not have, or was not reached.
- `area-aura-fidelity`: the remaining rank, friendliness/PvP and tick synchronization work belongs to
  `SpellSystem.AreaAuras.cs` (spell-breadth S2).
- `weapon-imbue-core`, `totem-held-item-enchant`, `rockbiter-weapon-damage`, `imbue-combat-procs`: not reached; imbues
  need SpellItemEnchantment.dbc import, a temp-enchant lifecycle and the items lane's enchant plumbing.
- Everything gated on the proc engine, script registry (`ISpellScript`), `ISpellCastCheck`, shapeshift service, aura-state
  service, immunity ledger or death lane: seals, Judgement, Holy Shock, Hammer of Wrath, blessings and Greater
  Blessings (target 61 now exists), paladin auras and Concentration, bubbles with Forbearance, Lay on Hands, Divine
  Intervention, creature-type targeting, Ghost Wolf, Lightning Shield, Reincarnation, Water Walking.
- Far Sight, Sentry Totem camera, Water Breathing: other lanes or no camera/breath system (Consecration and the persistent
  area auras: [class-scripts](class-scripts.md)).

## Tests
- `tests/ArcaneCore.Game.Tests/ClassSpells`: `LocationTargetTests` (13), `ShockCooldownGuardTests` (a labelled GUARD,
  green on arrival; mutating a rank's category turns it red), `Totems/TotemSummonTests` and `TotemLifecycleTests` (24).
- `Totems/ActiveTotemTests` (17): normal cast time and repeated casts, nearest/sticky/owner helper targets,
  creature targets, strict bounding-radius range, target loss, PvP gates, stealth and ordinary cast LOS,
  owner death on a long tick, and in-flight cleanup at logout/death/replacement/expiry. The initial 13-case suite
  produced 12 failures before active casting was implemented.
- `tests/ArcaneCore.Data.Tests/Totems/TotemSpellDataTests` (importer, schema step, EF round trip, real-data audit).
- `tests/ArcaneCore.World.Tests/ClassSpells/Totems/TotemLoopbackTests`: the real host end to end,
  including active cast-start packets, bolt damage through the world spell timer, and owner-death cleanup.
- `TotemImmunityTests`, `SpellMagnetTests`, `TotemImmunityWorldTests` and `GroundingTotemWorldTests`
  cover effect filtering, source exceptions, redirected packets, protection charges and lifecycle cleanup.
- Real-client checklist (not verifiable here): totem model and spawn animation, totem placement relative to the
  character, party buff icons from totem area auras, no totem bar.
