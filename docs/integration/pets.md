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
  destination (including a `TARGET_LOCATION_DATABASE` spell position), the others at random points in the radius; no destination: `radius` in front of the
  caster (the caster's own radius is added by `GetNearPoint`), or the caster's position at radius 0.
  Lifetime is TEMPSUMMON_TIMED_DEATH_AND_DEAD_DESPAWN (`TemporarySummon.cpp:201-218`) when the spell
  has a duration (killed at the timer unless in combat, retried every tick, the corpse then decays
  like any temporary creature) and TEMPSUMMON_DEAD_DESPAWN without one (only its death ends it).
* **SUMMON_CRITTER (97)**, vmangos `EffectSummonCritter` (`5400-5472`): players only; the same entry
  again just dismisses the mini pet, another entry replaces it; owned by the player but not its
  `UNIT_FIELD_SUMMON`; keeps the template level and NPC flags (`SelectLevel`, `5458`); without a
  destination it appears at the caster, with one it appears `PET_FOLLOW_DIST` (2) away at
  `MINI_PET_SUMMON_ANGLE` (pi/4) from the caster's facing (vmangos ignores the destination,
  `5433-5434`); faces the player (`5462`); learns its create spells (`InitPetCreateSpells`, `5452`).
  Pet.cpp's owner-gone, range, dead-owner and duration rules apply to guardians and mini pets as to pets
  (not the `IsControlled` pet-link rule). The 120-yard range rule skips a pet its owner possesses
  (`owner->GetCharmGuid() == GetObjectGuid()`, `Pet.cpp:670`). A dying player loses its pet and its mini
  pet at once, in combat or not (`Player::SetDeathState`, `Player.cpp:1527-1531`); its guardians keep the
  dead-owner rule (unsummoned once out of combat). A dead pet, guardian or mini pet is unsummoned when its
  corpse timer ends: 15 s, an hour for a hunter pet (`Pet::SetDeathState`, `Pet.cpp:649-653`). A guardian
  takes its owner's player-controlled and PvP flags (the tail of `InitStatsForLevel`, `Pet.cpp:1472-1479`).

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
  generator, attack validates the target (refused while the owner has a `SPELL_AURA_MOD_PACIFY` aura) and
  starts the fight through the pet's AI with the 10% talk or the
  aggro reaction; dismiss unsummons a summoned pet, mini pet or guardian, but leaves a hunter's pet alone, which is
  dismissed through the Dismiss Pet spell the client casts, "Hunter pets are dismissed with a spell with a cast time",
  review finding 32), `CMSG_PET_SET_ACTION` (the move/swap checks of `PetHandler.cpp:198-290`),
  `CMSG_PET_SPELL_AUTOCAST`, `CMSG_PET_STOP_ATTACK`, `CMSG_PET_CAST_SPELL` (the 1.12 layout: guid, spell,
  targets), `CMSG_PET_CANCEL_AURA`, `CMSG_PET_NAME_QUERY` (answered only for the matching pet number),
  `CMSG_PET_ABANDON` (a summoned pet is dismissed), `CMSG_REQUEST_PET_INFO`. A spell button runs the
  spell-system checks in vmangos order: unknown spell, not ready, not known, explicit target missing, a
  negative spell on itself, face the target, then the cast; failures answer `SMSG_PET_CAST_FAILED`.
  The caster of a pet spell is the pet itself.

### P5 PetAI

`Game/Pets/PetAI.cs`; every pet, guardian and mini pet gets it instead of the creature's own AI
(vmangos `CreatureAISelector`, `PetAI::Permissible` = every `IsPet()`); totems get `NullCreatureAI`.
Ported from `AI/PetAI.cpp`: `UpdateAI` (a valid victim is kept, otherwise the pet returns), `_needToStop`
(disabled pet, creature owner evading or out of the threat area, target no longer attackable),
`_stopAttack`, `HandleReturnMovement` (to the stay point, or follow at the stored angle), `MovementInform`
(arrival at the stay point; arrival at the follow point is the follow generator's `FOLLOW_MOTION_TYPE` inform
with the owner's low GUID, sent once its spline is finalized and never while the pet cannot move, as
`FollowMovementGenerator::Update` does), `DoAttack` (chase, or hold position while staying), `AttackStart` (as `AttackTarget`: the base
`CreatureAI.AttackStart` is not virtual), `CanAttack` in vmangos' order (passive, PvP-flagged targets,
returning, stay, switching targets, follow), `SelectNextTarget` and `KilledUnit` (the pet's own attackers,
then the owner's victim and attackers), `AttackedBy`, `OwnerAttackedBy`, `OwnerAttacked` and the imp's
missing melee attack (entry 416). `PetMapSystem` feeds the owner hooks from the map's damage event
(vmangos `Unit::AttackedBy`, `Unit::Attack`, `SetInCombatWithVictim`: `Unit.cpp:4541`, `4563`, `6080`);
`CMSG_PET_ACTION` attack goes through `AttackTarget`. Autocast: while the pet fights, a harmful, ready
spell with autocast on goes at the victim, chosen at random among those that qualify, with the aggro growl
(10%: the special-spell talk) for the owner (the harmful half of `PetAI.cpp:226-330`).

### P4 pet data: levelstats and create spells

`Kernel/WorldData/Pets/PetContent.cs`, `Data/World/Pets/PetWorldDataModule.cs`, `Game/Pets/PetInitializer.cs`,
the load in `World/Pets/PetsFeature.cs`.

* **Schema: World version 11** (`PetWorldDataModule.Version`, the next free world version of this tree: 10 is
  the quest reputation columns; the integrator renumbers the constant in merge order). Two tables, no
  character data (so no `ICharacterDataCleanup`): `pet_levelstats` (`Entry, Level, Health, Mana, Armor,
  DmgMin, DmgMax, Strength, Agility, Stamina, Intellect, Spirit`, key `(Entry, Level)`, the vmangos column
  set, `ObjectMgr.cpp:4410`) and `petcreateinfo_spell` (`Entry, Spell1..Spell4`). Importer mapping for the
  content-import lane: classic-db names the first table `creature_entry, level, hp, mana, armor, str, agi,
  sta, inte, spi` (no damage columns: `DmgMin`/`DmgMax` stay 0, which keeps the creature's own damage) and
  the second one exactly `entry, Spell1..Spell4`. vmangos also gates `petcreateinfo_spell` rows by
  `patch_min`/`patch_max` (`ObjectMgr.cpp:6475`); classic-db has no such columns. The classic-db dump also
  holds an entry `1` (the standard hunter pet stats); it is loaded like any other and used by nothing yet.
* **PetContent** follows `LoadPetLevelInfo`/`GetPetLevelInfo` (`ObjectMgr.cpp:4406-4516`): levels outside
  1..60 are ignored (60 is the vmangos `MaxPlayerLevel` default), an entry without level 1 data is a hard error
  (vmangos exits; here the world refuses to start), a level without data repeats the level below it, a lookup
  above 60 answers level 60. Create spells stop at the first 0 and an empty row is not kept.
* **InitStatsForLevel** (SUMMON_PET branch, `Pet.cpp:1274-1480`), when the summoned pet's creature has a row
  for the caster's level: the template's unit flags are dropped (`SetUInt32Value(UNIT_FIELD_FLAGS, NONE)`),
  melee damage when both bounds are non-zero, armor when non-zero, health, mana (the power type becomes mana)
  and the five stats; the owner's player-controlled and PvP flags are copied and health and mana end full.
  The rank health and damage rates of a creature owner (`_GetHealthMod`, `_GetDamageMod`) default to 1 in
  vmangos and are not configurable here.
* **InitPetCreateSpells** (`Pet.cpp:2051-2104`): the bar is reset, every spell of the row that exists is
  learned; a learn spell (SPELL_EFFECT_LEARN_SPELL or LEARN_PET_SPELL) stands for the spell it triggers; a
  passive spell is cast on the pet at once, known but off the bar (`Pet::AddSpell`, `Pet.cpp:1887-1975`); any
  other starts with autocast **off** (`ACT_DECIDE`), on the first free spell slot. The spell list reaches the
  owner in `SMSG_PET_SPELLS`; passive spells carry state 0x01.

### P6 who a pet acts for (hand-offs to the other lanes)

`Game/Pets/OwnerLinks.cs` also carries the vmangos resolution helpers: `GetCharmerOrOwner`,
`GetCharmerOrOwnerOrSelf`, `IsCharmerOrOwnerPlayerOrPlayerItself`, `GetCharmerOrOwnerPlayerOrSelf`,
`GetCharmerOrOwnerPlayer`, `GetAffectingPlayer` (`Unit.cpp:4795-4834`: a player acts for itself, a pet or totem
for its owner, a pet of a creature for the player that owns that creature) and `GetPet`. They are tested but
**not wired into other areas' code by this lane**: each caller below is a one-line change in the other
lane's file, listed here so the integrator can apply it where the lane that owns the file lands.

| Area (file) | What needs the helper | vmangos |
|---|---|---|
| loot (`Game/Loot/LootService.OnCreatureKilled`) | `killer is not Player` refuses a pet's kill: use `killer?.GetAffectingPlayer()` for the recipient | `Unit.cpp:981`, `1256` (`GetCharmerOrOwnerPlayerOrPlayerItself`) |
| experience, quest kill credit, reputation (group-loot-xp, npc-services-quests lanes) | the same killer resolution | `Unit.cpp:1256` |
| faction and PvP rules (`Combat/FactionCombatHooks.cs`, lines 31-32 list it as missing) | hostility of a pet or totem is its owner's: `Object.cpp:3745-3815`; copy of the owner faction is already on the unit, the player-controlled and PvP flags too | `Object.cpp:3745-3815` |
| stats (stats-combat-formulas lane) | the owner's stat inheritance for a pet (`Pet::UpdateAllStats`), `CreatureClassLevelStats` for pets without `pet_levelstats` | `Pet.cpp:1274-1480` |
| mounts (the owner's lane) | `PetController.SetEnabled(pet, false/true)` on mount and dismount (greys the bar) | `Player.cpp:18214`, `18249` |
| teleports and logout (teleport, character lanes) | done for teleports (`PetTeleportFollow`, wave 2); a logout keeps the hunter pet through the pet store (P7), a demon is not stored | `UnsummonPetTemporaryIfAny` |

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

### P7 persistent current hunter pet and effect 109

The 2026-10-04 continuation adds a thin current hunter-pet lifecycle in
`Game/Pets/PersistentPetService.cs`, `Data/Characters/Pets/PersistentPetDataModule.cs`, and
`World/Pets/PersistentPetFeature.cs`. Character loading reads the detached snapshot before
world entry through `ICharacterHooks`, then publishes synchronously on `PlayerLoggedIn`.
Writes are captured before logout/map removal and before corpse decay, serialized per character,
drained at shutdown, and drained/forgotten during character deletion. The stable pet number is
preserved in the charm state while each world spawn receives a fresh transient GUID counter.

Effect 109 (`SUMMON_DEAD_PET`) first revives a retained in-world corpse; otherwise it materializes
only a cached dead current hunter pet and applies `maxHealth * effectValue / 100`. Guardians,
demons, critters, wild summons, and static templates are excluded. The behavior is grounded in
vmangos `SpellEffects.cpp:5533-5570`, mangos-classic `SpellEffects.cpp:5163-5199`, and vmangos
`Objects/Pet.cpp:120-200`.

This slice does not claim complete hunter-pet parity. Pet spell cooldowns, saveable non-passive
auras, loyalty/training, tame/stable subtype, pet type, names, and their producers remain future
work; the existing generic spell persistence primitives currently operate only through the player
spell feature. See `work/pet-state-next.md` for the verified next-slice contract.

The [2026-10-04 pet revival continuation](pet-revival-20261004.md) implements
effect 113 for the retained corpse of the current controlled summoned pet. It
preserves the existing pet object and action bar, restores health, resets AI,
and removes the owner's Demonic Sacrifice override auras. This does not supply
a persistent pet instance store, effect 109, or guardian/mini-pet revival.

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
* **Map change (wave 2).** A far teleport puts the controlled pet away before the owner leaves its map and brings it back in the
  new map; a same-map teleport does so when the pet is beyond the grid activation distance of the destination
  (`PetTeleportFollow`, `SummonService.UnsummonPetTemporarily` / `ResummonTemporarilyUnsummonedPet`; vmangos
  `UnsummonPetTemporaryIfAny`, `ResummonPetTemporaryUnSummonedIfAny`, Player.cpp:1911-1921, 2045-2048, 20911-20940,
  MovementHandler.cpp:197-198, 274-284). A hunter pet comes back from its current-pet snapshot; a demon or other summoned pet keeps
  its pet number, health, mana, react state, name, spells and bar (held in memory, since demons are not stored). Totems, guardians
  and mini pets are unsummoned and stay gone (vmangos `RemoveFromWorld`); a temporary summon and a dead pet are not brought back (a
  dead hunter pet stays dead in its snapshot, where Revive Pet finds it; vmangos would reload it as a corpse). Not done: the taxi,
  mount and possess triggers of the same pair.
* **Spirit guide re-summon (wave 2).** `SummonService.AutoReSummonPet` (vmangos `Player::AutoReSummonPet`, Player.cpp:1580-1628) runs
  on `SpellSystem.PlayerSpiritHealed` (SPELL_EFFECT_SPIRIT_HEAL): the last demon or loaded permanent pet (`m_petEntry`, `m_petSpell`)
  comes back when the summoning spell's reagents are in the bags (they are taken); a pet saved dead is brought back to life at full
  health, a living one keeps its saved health (Player.cpp:1619-1628). A hunter's abandon forgets it, and so does a logout
  (`SummonService.ForgetOwner` on `WorldRuntime.PlayerLoggingOut`: vmangos keeps it on the Player object). A warlock gets a fresh demon
  of that entry. A hunter pet carries no taming spell here, so the vmangos "no spell, no pet" guard does not apply to it.
* **Reagents back (wave 2).** A pet whose owner dies, that lost its owner, was left beyond the leash or whose owner has no pet any
  more gives its summoning spell's reagents back (`Pet::Unsummon(PET_SAVE_REAGENTS)`, Pet.cpp:668-674, 1052-1075). Not done: the
  talent reset, taxi and far-control triggers of the same mode.
* **Pet data gaps.** A pet whose creature has no `pet_levelstats` row keeps its template health, damage,
  armor and stats: vmangos falls back to `creature_classlevelstats` (`GetClassLevelStats`), a table of the
  stats lane that this build does not have. Not ported: the owner's stat inheritance and `UpdateAllStats`
  (the stats lane), the template's school resistances, `LearnPetPassives` (skill-line passives),
  `CastPetAuras` (`spell_pet_auras`), the owner's teach spells (`AddTeachSpell`: the player learning the
  summoned demon's spells), `GetPetCreateSpellEntry` rows by creature spell list (`pet_spell_list_id`), the
  hunter pet branch and `pet_name_generation`. The pet name is the creature name, not the family name of
  `CreatureFamily.dbc` (`InitializeDefaultName`).
* **Pet spell lists.** A summoned pet starts with no spells: its spell list comes from
  `petcreateinfo_spell` / the creature spell list (vmangos `InitPetCreateSpells`), the data of slice P4.
  `CharmInfo.LearnSpell` is the entry point; rank chains (`GetFirstSpellInChain`) are not modelled, so a new
  rank takes its own bar slot. `CMSG_PET_RENAME`, `CMSG_PET_UNLEARN` and the stable opcodes are hunter pet
  features (class-hunter lane); `CMSG_PET_CAST_SPELL` has no `CheckPetCast` range-facing refinements beyond
  what the spell system checks; `PetBroken` and the tame-failure packet belong to taming.
* **PetAI gaps.** Not ported: positive and ally autocast (`Spell::CanAutoCast`, `UpdateAllies`: Blood Pact,
  Fire Shield, Devour Magic and the like never autocast), taunt targets, the threat-list retarget of
  creature-owned pets, crowd-control checks (`HasAuraPetShouldAvoidBreaking`), possession, the caster
  chase distance of a spell-only pet (`SetCasterChaseDistance`), and aggro on sight: the creature area does
  not model creature-versus-creature aggro, so an aggressive pet reacts to attacks on itself and on its
  owner, not to a hostile that merely stands nearby. Also from the creature area: a same-faction creature
  asking for assistance can recruit a pet (the assistance filter only excludes `NullCreatureAI`).
* **Guardian level scaling.** The engineering-trinket level (`SpellEffects.cpp:2824-2832`) needs the
  skills lane's skill values; a guardian's stats follow its template, not `InitStatsForLevel` (only its
  owner-flag tail runs).
* **Random points** use a uniform disc at the terrain height (or the centre's Z), as the creature
  wander does, instead of the navmesh walk query of `GetRandomPoint` (`Object.cpp:1991-2063`).
* **Summon limit.** vmangos `SummonCreature` refuses once the summoner has too many active summons
  (`GetCreatureSummonLimit`, `Object.cpp:2445`, `2557`); not modelled for wild summons.
* **SUMMON_PET (56)** is not registered: vmangos `EffectSummonPet` (`SpellEffects.cpp:3171`) loads the
  saved pet or creates a warlock demon through `LoadPetFromDB`/`CreateBaseAtCreature`, which needs
  the pet store (P7). **SUMMON_POSSESSED** and charm are also outside this build.
* Hunter taming, feeding, loyalty, stable and talents belong to the class-hunter and talents lanes;
  warlock summon kits to class-casters.

## Wave-2 integration note: totems

The totem implementation of this lane (effects 74 and 87-90, the slot table, `PetMapSystem` totem update, `ITotemSpellSource`,
`SummonKind.Totem`) was **removed** at integration: the shaman lane's `Game/Totems/TotemSystem` is the single totem
implementation (it owns effects 74, 87-90 and 110, the `totem_spell` world table, the passive totem spell and its auras
incl. party removal, and no kill XP/credit for totems). This lane keeps pets, guardians, mini pets and wild summons.
`Creature.IsTotem` asks `TotemQuery`. Owner links (`OwnerLinks`) work for totems because both lanes use the same update
fields. The limits above that mention totem spells, placement radii and `ITotemSpellSource` therefore no longer apply to this
lane; see `docs/areas/class-shaman-paladin.md` for the totem limits. The lane's `TotemTests` were deleted with the code;
the same behaviours (placement, slots, duration, ownership) are covered by `tests/ArcaneCore.Game.Tests/ClassSpells/Totems`.
`pet_levelstats` / `petcreateinfo_spell` is World schema 16.
