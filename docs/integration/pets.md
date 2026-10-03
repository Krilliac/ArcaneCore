# Pets, guardians, mini pets and totems (lane `pets`, wave 2)

Standing directive: everything as close to vanilla 1.12.1 as the real references allow, mechanics and
data. References (GPL, read-only; nothing is copied into the repo): vmangos
(`D:\refs\vmangos`, primary), wow_messages (`D:\refs\wow_messages`, wire layouts), mangos-classic,
classic-db. Every behaviour below cites the vmangos file and line it was checked against.

## Delivered scope

### P1 summon model and totems

| Piece | Where | Reference |
|---|---|---|
| Summon kinds, totem slots (FIRE 0, EARTH 1, WATER 2, AIR 3, NONE 255), options | `Game/Pets/PetDefines.cs` | `SharedDefines.h:1727-1735` |
| Owner links over update fields: `OwnerGuid` (UNIT_FIELD_SUMMONEDBY 0xC), `CreatorGuid` (0xE), `PetGuid` (UNIT_FIELD_SUMMON 0x8), `CharmerGuid` (0xA), `CharmerOrOwnerGuid`; the fields are the source of truth | `Game/Pets/OwnerLinks.cs` | `Unit.h:1195-1233` |
| Server-side summon record (kind, owner, spell, slot, time left) held in `Creature.Summon`; `Creature.IsTotem` / `IsPet` | `Game/Pets/SummonLinks.cs`, `Creatures/Creature.cs` | `Totem.cpp`, `Pet.cpp` |
| Spawn path that takes a `HighGuid` and lets the caller fill the owner links before the create block is built | `Creatures/CreatureMapSystem.Summons.cs` (new file; `SpawnTemporary` and the creature-ai `_summons` list are untouched) | `SpellEffects.cpp:4923-5007` |
| Totem effects 74 (SUMMON_TOTEM, no slot) and 87-90 (slots 1-4) | `Game/Pets/SummonService.cs` | `SpellEffects.cpp:4923-5007` |
| `PetMapSystem` (per map, `[DefaultMapUpdater(Order = 150)]`): totem slot table, duration and rule checks, owner-removed cleanup | `Game/Pets/PetMapSystem.cs` | `Totem.cpp:66-92`, `Pet.cpp:662-712` |
| `SummonService : ISpellSummonSink`, so SPELL_EFFECT_SUMMON (28) and the quest reward preflight reach production code; the built-in effect 28 handler is kept (`HasBuiltInEffectHandler` still true) | `Game/Pets/SummonService.cs`, `World/Pets/PetsFeature.cs` | `SpellEffects.cpp:2334-2450` |
| `PetsFeature` (discovered `IWorldFeature`) installs the effects on the spell system, binds the `Pets` options and is the DI `ISpellSummonSink` | `World/Pets/PetsFeature.cs` | |

Totem behaviour, in vmangos order (`SpellEffects.cpp:4923-5007`, `Totem.cpp`):

* the totem already in the slot is unsummoned first (slots only; effect 74 has none);
* placed at `owner + (owner radius + 2.0 + totem radius)` along `owner facing + pi/4 - slot * pi/2`,
  Z from the terrain (+0.05) when the map has height data, else the owner's Z, forced to the owner's
  Z when more than 5 yd apart (`Totem.cpp:47`, `Object.cpp:2726-2790`);
* **HIGHGUID_UNIT**, never HIGHGUID_PET (vmangos uses `HIGHGUID_UNIT` for totems and `HIGHGUID_PET`
  for pets, guardians and critters); owner and creator fields, faction and level copied from the
  owner; `UNIT_CREATED_BY_SPELL`; player-controlled flag for a player owner; PvP flag copied;
  a non-zero effect value is the totem's health;
* `SMSG_GAMEOBJECT_SPAWN_ANIM {guid}` on summon and `SMSG_GAMEOBJECT_DESPAWN_ANIM {guid}` on unsummon
  (`Object.cpp:2352-2364`). There is no `SMSG_TOTEM_CREATED` in 1.12 (wow_messages
  `smsg_totem_created.md` has no Client Version 1 section), so none is sent;
* it lives for the spell duration, and goes when its owner dies (a creature owner excepted), is gone
  or out of visibility range, or when the totem is killed (`Totem.cpp:66-92`); a zero duration ends
  at once, as in vmangos; removing the owner from the map removes its totems.

Pet behaviour through the sink (SPELL_EFFECT_SUMMON 28, `SpellEffects.cpp:2334-2450`): a caster that
already has a pet summons nothing; the pet is a HIGHGUID_PET creature facing `-caster orientation`,
owner/creator links, owner faction and level, `UNIT_FIELD_SUMMON` set on the owner, NPC flags cleared,
`UNIT_CREATED_BY_SPELL`. Unsummoned (`Pet.cpp:662-712`) when the owner is missing, farther than
`Pets:PetLeashDistance` (120, by the 3D distance less both radii), no longer lists it as its pet,
dead with the pet out of combat, or when the spell duration ends.

### P2 guardians, wild summons and mini pets

`Game/Pets/SummonService.Guardians.cs`; registered by `SummonService.Install`.

* **SUMMON_GUARDIAN (42)**, vmangos `EffectSummonGuardian` (`SpellEffects.cpp:2775-2914`):
  HIGHGUID_PET creatures that are owned (owner and creator fields, owner faction, name timestamp 0,
  created-by-spell) but **not** the owner's `UNIT_FIELD_SUMMON` pet. A direct (not triggered) second
  cast by a player dismisses its guardians of that entry and stops there, unless the spell has both a
  duration and a category (`2791-2804`); a non-player caster is capped at 16 guardians of an entry
  (refused once it has more than `MaxNpcGuardiansPerEntry` = 15, `2806`); the level is the template's
  `urand(level_min, level_max)`, or for a non-player caster with `EffectMultipleValue <= 0` the
  caster level plus that value when it is 1..63 (`2810-2822`); `damage` guardians (at least 1), the
  first at the destination facing `-orientation`, the others on a random point within the effect radius
  of the centre facing `+orientation`, or all at the caster when there is no destination
  (`2851-2865`); follow angle `pi/2 + pi/6 * (guardians + pet)` wrapped below `2 pi` (`2889-2897`),
  stored in `SummonLinks.FollowAngle`. The NPC flags stay the template's.
* **SUMMON_WILD (41)**, vmangos `EffectSummonWild` (`2685-2772`) through `SummonCreature`
  (`Object.cpp:2547-2610`): HIGHGUID_UNIT temporary summons without owner or creator fields (`2761`),
  template faction and level, `UNIT_CREATED_BY_SPELL`; `damage` of them (at least 1), the first at the
  destination, the others at random points in the radius; no destination: `radius` in front of the
  caster (the caster's own radius is added by `GetNearPoint`), or the caster's position at radius 0.
  Lifetime is TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN (`TemporarySummon.cpp:201-218`) when the spell
  has a duration (killed at the timer unless in combat, retried every tick, the corpse then decays
  like any temporary creature) and TEMPSUMMON_DEAD_DESPAWN without one (only its death ends it).
* **SUMMON_CRITTER (97)**, vmangos `EffectSummonCritter` (`5400-5472`): players only; the same entry
  again just dismisses the mini pet, another entry replaces it; owned by the player but not its
  `UNIT_FIELD_SUMMON`; keeps the template level and NPC flags (`SelectLevel`, `5458`); without a
  destination it appears at the caster, with one it appears `PET_FOLLOW_DIST` (2) away at
  `MINI_PET_SUMMON_ANGLE` (pi/4) from the caster's facing (vmangos ignores the destination,
  `5433-5434`); faces the player (`5462`). Pet.cpp's owner-gone, range, dead-owner and duration rules
  apply to guardians and mini pets as to pets (not the `IsControlled` pet-link rule).

### P3 pet wire protocol and commands

`Game/Pets/{CharmInfo,PetPackets,PetController}.cs`, `World/Pets/PetHandlers.cs`.

* **CharmInfo** (vmangos `CharmInfo`, `Unit.cpp:8338-8650`): command state (a new pet follows), react
  state (a mini pet passive, a guardian aggressive, a summoned pet defensive for a player owner and
  aggressive for a creature owner, `SpellEffects.cpp:2400-2404`), the ten-slot action bar (attack, follow,
  stay, four spell slots, aggressive, defensive, passive; `InitPetActionBar`), the pet number, the
  stay/follow/returning/command flags, and the pet's spell list with autocast state. Every pet, guardian
  and mini pet has one (`Pet::Pet`: "pets always have a charminfo"). The pet number is also the entry part
  of the HIGHGUID_PET GUID (`Pet::Create`, `Object::_Create(guidlow, petNumber, HIGHGUID_PET)`);
  `UNIT_FIELD_PETNUMBER` stays 0 (`SetPetNumber(n, false)`). Pets also carry the misc byte flags
  `0x08 | 0x10 | 0x20` and a mini pet `IMMUNE_TO_PLAYER | IMMUNE_TO_NPC` (`Pet.cpp:2264-2268`).
* **Server packets** (`PetPackets`): `SMSG_PET_SPELLS` on summoning a pet (`Player::PetSpellInitialize`,
  `Player.cpp:17354-17398`; a guardian or mini pet gets none) and the empty one (GUID 0) when a pet is
  unsummoned (`Pet.cpp:1085`, `Player.cpp:17507`); `SMSG_PET_MODE` (`Pet::SetEnabled`, the greyed bar of
  a mounted owner; `PetController.SetEnabled`, to be called by the mount code); `SMSG_PET_ACTION_FEEDBACK`,
  `SMSG_PET_CAST_FAILED` (1.12 form: u32 spell, u8 2, u8 result), `SMSG_PET_NAME_QUERY_RESPONSE`,
  `SMSG_PET_ACTION_SOUND`, `SMSG_AI_REACTION`. Layouts are the gtker/wow_messages ones
  (`world/pet/*.wowm`, `queries/*pet_name*.wowm`; the name query and its response are checked against the
  published test vectors). Two deliberate readings: the **enabled byte** is `0x0` for an enabled pet and
  `0x8` for a disabled one (vmangos `IsEnabled() ? 0x0 : 0x8`; wow_messages names the byte the other way
  round, but a normal pet has to send 0 or its bar is greyed), and the **cooldown list** of
  `SMSG_PET_SPELLS` is u8 count then u16 spell, u16 category, u32 cooldown, u32 category cooldown, as in
  wow_messages and mangos-classic (`Unit::CharmCooldownInitialize`); vmangos' `WritePetSpellsCooldown`
  writes a u16 count and a u32 spell id, which agrees only for an empty list. The pet's running cooldowns
  come from the spell system (`SpellSystem.GetActiveCooldowns`).
* **Client opcodes** (`PetHandlers`, in-world handlers calling `PetController`): `CMSG_PET_ACTION`
  (commands, reactions, spell buttons: `HandlePetAction`, `PetHandler.cpp:35-165`; the commands follow
  `Unit::HandlePetCommand`, `Unit.cpp:8646-8764`: stay saves the stay position, follow pushes the follow
  generator, attack validates the target and starts the fight through the pet's AI with the 10% talk or the
  aggro reaction, dismiss unsummons), `CMSG_PET_SET_ACTION` (the move/swap checks of `PetHandler.cpp:198-290`),
  `CMSG_PET_SPELL_AUTOCAST`, `CMSG_PET_STOP_ATTACK`, `CMSG_PET_CAST_SPELL` (the 1.12 layout: guid, spell,
  targets), `CMSG_PET_CANCEL_AURA`, `CMSG_PET_NAME_QUERY` (answered only for the matching pet number),
  `CMSG_PET_ABANDON` (a summoned pet is dismissed), `CMSG_REQUEST_PET_INFO`. A spell button runs the
  spell-system checks in vmangos order: unknown spell, not ready, not known, explicit target missing, a
  negative spell on itself, face the target, then the cast; failures answer `SMSG_PET_CAST_FAILED`.
  The caster of a pet spell is the pet itself.

## Configuration (`Pets`, every default is the retail value)

| Key | Default | Meaning |
|---|---|---|
| `PetLeashDistance` | 120 | `Pet.cpp:662-690` |
| `MaxNpcGuardiansPerEntry` | 15 | `SpellEffects.cpp:2806` (used by the guardian slice) |

## Extension points other lanes use

* `OwnerLinks` (`unit.CharmerOrOwnerGuid`, `unit.GetOwner()`): what the faction lane's
  `FactionCombatHooks` owner resolution (`Object.cpp:3745-3815`) needs; this lane does not edit
  `FactionCombatHooks`.
* `ISpellSummonSink.Summon(Unit, in SpellSummonRequest)`: an additive default-interface overload that
  carries the summoning spell id. Sinks that only implement the original member keep working.
* `ITotemSpellSource`: the spell a totem casts. See limits.
* `map.Pets` (`PetMapSystem`): `GetTotem(owner, slot)`, `GuardiansOf`, `SummonsOf`, `Summons`.

## Shared-file edits (for the integrator)

| File | Change |
|---|---|
| `Game/Creatures/Creature.cs` | optional `HighGuid` constructor parameter (default `Unit`), internal `Summon` property, `IsTotem`, `IsPet` |
| `Game/Spells/SpellTargetingSeams.cs` | `SpellSummonRequest` and an additive default `Summon(Unit, in SpellSummonRequest)` member on `ISpellSummonSink` |
| `Game/Spells/SpellSystem.Combat.cs` | `EffectSummon` calls the request overload (the vmangos behaviour is unchanged) |
| `World/Features/WorldFeatures.cs` | `ISpellSummonSink` added to the seam interface list so a feature can be the sink |

## Limits (what is not here, and why)

* **Totem spells.** vmangos reads `creature_template.totem_spell_id` (`Totem.cpp:220-223`). The
  `creature_template` of this build has no such column and classic-db does not carry it (cmangos
  reads the creature spell list). Until the content importer provides it, `ITotemSpellSource` has no
  implementation in the daemon and totems are visual only (one warning at the first summon). With a
  source, a passive totem (spell without cast time) casts on itself at once and its auras are
  removed from the totem, the owner and the owner's party on unsummon (`Totem.cpp:126-146`).
  Active totems (a spell with a cast time, `Totem.cpp:168-178`) need `TotemAI`, which needs the
  creature-ai targeting primitive: not built, logged once per spell, the totem stays idle. Totem
  immunities (`Totem.cpp:180-205`) are not ported.
* **Placement.** Only the primary candidate of vmangos `GetNearPoint` is used. The search for a free
  spot around the owner (`ObjectPosSelector`) and the line-of-sight retry are not ported.
* **Pet stats and name.** A summoned pet keeps its template health and damage: `InitStatsForLevel`
  (`Pet.cpp:1274-1480`) needs the stats lane's per-class level stats and the `pet_levelstats` data
  (a World table, planned P4). The pet name and `UNIT_FIELD_PETNUMBER` are not generated.
* **Map change.** A pet or totem is unsummoned when its owner leaves the map (including a far
  teleport). vmangos re-summons a temporarily unsummoned pet after the transfer
  (`UnsummonPetTemporaryIfAny`); that belongs with pet persistence (P7).
* **Pet spell lists.** A summoned pet starts with no spells: its spell list comes from
  `petcreateinfo_spell` / the creature spell list (vmangos `InitPetCreateSpells`), the data of slice P4.
  `CharmInfo.LearnSpell` is the entry point; rank chains (`GetFirstSpellInChain`) are not modelled, so a new
  rank takes its own bar slot. `CMSG_PET_RENAME`, `CMSG_PET_UNLEARN` and the stable opcodes are hunter pet
  features (class-hunter lane); `CMSG_PET_CAST_SPELL` has no `CheckPetCast` range-facing refinements beyond
  what the spell system checks; `PetBroken` and the tame-failure packet belong to taming.
* **No pet AI yet.** Pets, guardians and mini pets run the creature-ai default AI for their template
  (AggressorAI fights back when attacked; creature-versus-creature aggro is not modelled there
  either). vmangos `PetAI` (follow at the stored angle, defend, assist the owner, the commands and
  react states) is tied to `CharmInfo` and the pet wire protocol and is a later slice that also waits
  for the creature-ai leash and aggro work. The first consequence: nothing follows its owner, and an
  attacked guardian runs the generic assistance call.
* **Guardian level scaling.** The engineering-trinket level (`SpellEffects.cpp:2824-2832`) needs the
  skills lane's skill values; a guardian's stats follow its template, not `InitStatsForLevel`.
* **Random points** use a uniform disc at the terrain height (or the centre's Z), as the creature
  wander does, instead of the navmesh walk query of `GetRandomPoint` (`Object.cpp:1991-2063`).
* **Summon limit.** vmangos `SummonCreature` refuses once the summoner has too many active summons
  (`GetCreatureSummonLimit`, `Object.cpp:2445`, `2557`); not modelled for wild summons.
* **SUMMON_PET (56)** is not registered: vmangos `EffectSummonPet` (`SpellEffects.cpp:3171`) loads the
  saved pet or creates a warlock demon through `LoadPetFromDB`/`CreateBaseAtCreature`, which needs
  the pet store (P7). **SUMMON_POSSESSED** and charm are also outside this build.
* Hunter taming, feeding, loyalty, stable and talents belong to the class-hunter and talents lanes;
  warlock summon kits to class-casters.
