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
* Hunter taming, feeding, loyalty, stable and talents belong to the class-hunter and talents lanes;
  warlock summon kits to class-casters.
