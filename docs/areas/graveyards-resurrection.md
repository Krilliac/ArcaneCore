# Graveyards and resurrection

Lane `graveyards-resurrection` (wave 4). Everything is checked against the real references (`D:\refs\vmangos` primary,
`mangos-classic`, `wow_messages`, `classic-db`); no code or data is copied, and no GPL data is committed. File:line
citations are in the code comments; the list below is the map.

The death area before this lane (waves 1-3) had the dying and the corpse run but nowhere to run from: a released spirit stayed
on its body, nobody could resurrect anybody, and the ghost was a flag. This lane adds the graveyards, the ghost, the living/dead
separation, resurrection by spell and self-resurrection.

## Delivered

### Seams and the scheduled repop (`DeathSeams`)

`src/ArcaneCore.Game/Death/DeathSeams.cs` is a per-world registry (weak side table, like `DeathHooks`) of the extension points the
death area adds: `IGraveyardRepop` and `IGhostForm`. It exists because `CombatHooks` takes one production registration (the sealed
`FactionCombatHooks`) and `DeathHooks` can be replaced wholesale; each seam is filled once by the feature that owns it. The
`CombatHooks` defaults `RepopAtGraveyard`, `ApplyGhostForm` and `RemoveGhostForm` ask the seam and do nothing without one, so a
world without graveyard data behaves as before.

The repop is **scheduled**, as vmangos does: `RepopPlayer` sets a pending flag (`Player::ScheduleRepopAtGraveyard`,
Player.cpp:4980-4986) and the player tick runs it once no movement change is pending (Player.cpp:1329-1334: the ghost's water-walk
order, and the speed changes of the ghost aura, must be answered first). The flag is cleared before the trip (Player.cpp:4992), so a
miss is not retried. The undermap and fatigue paths keep calling the hook immediately. A logging-out spirit repops synchronously and
only the **saved** position moves (`Player.LogoutLocation`, used by `CreateSnapshot`); the corpse snapshot keeps the death spot
(WorldSession.cpp:694-701). `MapCombat.IsInstanceableMap` reads the map template when one is loaded.

### Graveyard data (`world_safe_locs`, `game_graveyard_zone`)

World schema step `GraveyardDataModule` (version constant `GraveyardDataModule.Version`, provisional **21**; the integrator
renumbers). `GraveyardDumpImporter` reads both dialects: cmangos classic-db (`ghost_loc`, `link_kind`, the facing is
`world_safe_locs.o`) and vmangos (`ghost_zone`, `patch_min`/`patch_max` kept only when they cover patch 10, a separate
`world_safe_locs_facing` table, safe locations from `WorldSafeLocs.dbc` through `WorldSafeLocsDbcReader`, format `nifffxxxxxxxxx`,
DBCfmt.h:90). Load rules are vmangos' (`LoadGraveyardZones`, ObjectMgr.cpp:7453-7510): a faction other than 0, 67 or 469 is skipped, a
second link for the same graveyard and zone is skipped (the first stays), a `link_kind` other than 0 (a cmangos map link) is skipped and
counted; links to missing safe locations or zones are skipped when the catalog is built (`GraveyardCatalog.Build`), because the tables
arrive in any order. A name over 50 characters is cut and reported. The CLI reads the three tables with the others and the safe
locations from `--dbc-dir`'s `WorldSafeLocs.dbc` when present. **Local evidence:** `arcane-content-importer import --dry-run` against
classic-db z2815 reads 122 safe locations and 191 links (cmangos dialect detected).

### Choosing and going (`GraveyardSelector`, `GraveyardRepopService`)

`GraveyardSelector.FindClosest` is vmangos `GetClosestGraveYard` / `GetClosestGraveYardForArea` (ObjectMgr.cpp:7512-7645) as a pure
function: the area's links, then the zone's (only when the area is not the zone); enemy-faction links skipped (team 0 matches all);
on the player's map the nearest by 3D distance, else on the map of the dungeon's ghost entrance the nearest by 2D distance to the
entrance, else the last other-map link seen. No match: the ghost stays where it is. `GraveyardRepopService` sends the ghost there with
a teleport (the ghost state lives on the player, so it survives a far one), facing the safe location's facing, 0 when the table has none
(`GetWorldSafeLocFacing`); a spirit released on a transport comes back alive at once (Player.cpp:5000-5005); battlegrounds ask
registered `IGraveyardOverride`s (the battleground area implements it; without one the spirit stays). `GraveyardFeature` loads the data
and registers the service. **Deviation (config, default off, `World:Death:GraveyardFallbackToDefaults`):** where no graveyard is linked
for the team, mangos-classic sends the ghost to safe location 4 (Alliance) or 10 (Horde); vmangos leaves it. In classic-db 17 of the 100
zone/area keys have a graveyard for one faction only, so with the retail default the other faction's ghost stays at its body there.

### Spirit healer finish (`ResurrectAtSpiritHealer`)

After the 50% resurrection the player is teleported to the graveyard nearest its **corpse** when that differs from the one nearest to
where it stands, keeping its own orientation unless the safe location has a facing (NPCHandler.cpp:430-471). The resurrection sickness
starts at `World:Death:SicknessLevel` (default 11, vmangos `Death.SicknessLevel`; -10 gives full sickness at level 1, above the maximum
level none).

### Ghost form (`GhostForm`, `GhostAuras`)

A released spirit gets spell 8326 (aura 95 ghost, +25% run and swim speed) and, with the passive 20585, the wisp 20584
(`Player::ApplyGhostForm`, Player.cpp:4561-4577); `GhostAuras` is the handler of aura 95 (`HandleAuraGhost`, SpellAuras.cpp:5639-5659: the
visibility byte and `PLAYER_FLAGS_GHOST`). Water walking is still ordered separately, as in vmangos, and is no longer gated on the flag being
clear (the aura sets it first). Resurrection removes both spells; a stored ghost gets the aura back at login. The wisp's transform
(aura 56) has no handler yet and stays inert. `World:Death:GhostFormAura=false`, or a spell store without 8326 (logged once), falls back to the
earlier flag-only ghost. The ghost run-speed options (`Locomotion:GhostRunSpeed*`, including vmangos' quirk that they only apply in the
CORPSE state) already existed and are untouched. The `GhostAuras` registration of aura 95 collides at startup with any other lane that
registers it.

### Ghosts and the living (`GhostVisibilityRule`)

Per map, like the stealth rule (Player.cpp:18710-18743, Creature.cpp:2477-2509, Unit.cpp:7703-7710): a living player sees living players and
no ghosts; a ghost sees friendly ghosts, and living players within 100 yd of its own corpse; the same raid sees ghosts at any
distance; a game master sees everything; a living player cannot see a spirit healer or guide (npc flag 0x20/0x40), a ghost sees them and
the living creatures within (20+25) x the creature aggro rate of its corpse. A player still waiting at its body (CORPSE state) counts as
living. The release, the resurrection and the repop refresh the player's visibility both ways.

### Death by a creature loses durability (`ApplyDeathDurabilityLoss`)

10% of the worn items and an empty `SMSG_DURABILITY_DAMAGE_DEATH`, unless a player (or a unit that acts for one) killed the player, or
the map is a battleground (Unit.cpp:1190-1202). Self kills keep the environmental path's own loss, so nothing is lost twice;
the existing `DurabilityLossEnable` item option gates the points, not the packet.

### Resurrection by spell (`ResurrectEffects`, `ResurrectionService`)

Effects 18 (`RESURRECT`, a percent, dithered) and 113 (`RESURRECT_NEW`, health and mana from the spell) offer the dead player a
resurrection (SpellEffects.cpp:209-263, 5228-5251): a corpse target resolves to its owner on any map (Spell.cpp:3109-3118), the cast
check wants the corpse in the caster's map and in its line of sight (Spell.cpp:5780-5790), a second request while one is pending is ignored,
and `SMSG_RESURRECT_REQUEST` is sent (caster guid, name only for non-player casters, sickness byte = caster is a spirit healer,
delayed byte = no `AttributesEx3 0x10`: Rebirth is instant). `CMSG_RESURRECT_RESPONSE` accepts only the player that asked
(MiscHandler.cpp:605-622); accepting teleports the ghost to a **player** resurrector first (a dungeon instance it is no longer bound to is
replaced by the dungeon's entrance trigger, then its go-back trigger, then its own place: Player.cpp:20069-20091), the resurrection
waits for the teleport to finish (`TeleportService.TeleportCompleted`), then health and mana are the offered ones (capped), rage is 0, energy
full, and the corpse is gone. The request survives until the next death or a decline, like vmangos' `m_resurrectData`. **Packet layout:**
vmangos and mangos-classic send two trailing bytes (sickness, delayed); gtker's `smsg_resurrect_request.wowm` lists one `Bool`. The two
servers are followed, in one writer (`ResurrectionPackets.BuildRequest`); a real 1.12 capture would settle it.

### Self-resurrection (`SelfResurrection`, `SelfResurrectEffect`)

At death `PLAYER_SELF_RES_SPELL` gets the spell the player could resurrect itself with, chosen before the death strips the auras
(Player.cpp:19868-19945, 1507-1570): a Soulstone buff (20707, 20762-20765 -> 3026, 20758-20761), Twisting Nether (23701, 10% roll -> 23700), or
Reincarnation (20608 known, an Ankh 17030 in the bags, 21169 ready -> 21169). The loop is vmangos' including its order quirk: a Twisting
Nether after a Soulstone wins when its roll succeeds, the other order does not. `CMSG_SELF_RES` casts the stored spell on the player (not
triggered, so Reincarnation's hour of cooldown starts) and empties the field; any other resurrection empties it too. Effect 94 is flat for a
negative value (health = -value, mana = misc value) and a percent otherwise. Reincarnation's Ankh is taken in the CMSG_SELF_RES path once
the cast went through, because the spell system has no reagent step.

### Corpses and dungeons (`CorpseQuery`, `GhostEntryRules`)

`MSG_CORPSE_QUERY` shows a corpse in a dungeon, seen from another map, at the dungeon's ghost entrance (map, x, y, the ground height of the
entrance map, and the corpse's real map last; QueryHandler.cpp:155-201). A ghost may only enter, at an area trigger, a dungeon its corpse is in
or one the corpse's dungeon is nested in; otherwise "You cannot enter %s while in ghost form."; with the corpse in an inner dungeon it lands at
that dungeon's entrance (MiscHandler.cpp:712-756). A ghost that far-teleports into the map its corpse lies in is resurrected at half health on
the way (Player.cpp:1953-1966). The Molten Core special case of patches up to 1.2 is not modelled (this core is 1.12).

### GM commands

`.revive [name]` (half health and mana, corpse gone), `.gocorpse [name]`, `.neargrave [alliance|horde]`, at the vmangos levels. `.neargrave`
reproduces vmangos' lookup of the link in the player's **zone** (a graveyard linked only to an area answers with the "fix your DB" text, 454).
Texts are mangos_string 164 and 454-461 from classic-db; the text of `.revive` (vmangos string 5031) is not in the reference tree and is ArcaneCore's
own wording.

## Limits (not delivered)

- **Bones and corpse appearance.** A resurrected player's corpse leaves the world; bones objects, `Death.Bones.*`, the 60-minute bones
  expiry, the 3-day corpse expiry and the corpse's equipment/guild/helm fields (`CORPSE_FIELD_ITEM`, vmangos CreateCorpse) are not modelled.
- **Battlegrounds.** `IGraveyardOverride` is the seam; the "waiting to resurrect" spell, the area spirit healer queue and the 100% reclaim
  belong to the battleground area. A spirit in a battleground map stays where it is without an override.
- **Hot reload and GM link editing.** `WorldGraveyards.Build`/`Replace` swap the catalog atomically, but nothing registers a
  `.reload game_graveyard_zone` yet, and `.linkgrave` (which writes the database in vmangos) and the dead `.unstuck` branch are not provided.
- **Offline `.revive`** answers "Player not found!" (vmangos converts the offline corpse); the pet branch of RESURRECT_NEW (a dead pet target)
  is not modelled; the request is in memory only.
- **Durability** is not skipped for `SPELL_ATTR_EX3_NO_DURABILITY_LOSS` spells (`Kill` is not told the spell).
- **Creature visibility flags** (`CREATURE_FLAG_EXTRA_INVISIBLE`, `VISIBLE_TO_GHOSTS` on creatures other than the spirit services) and
  the creature corpse decay timer are not in the content; the npc flags stand in for the healers' ghost aura 9036 (not imported).
- **Saved zone.** A spirit that logs out is saved at its graveyard's position but with its old zone id.
- **Revival at a dungeon door** happens when the far teleport is accepted; vmangos does it before the entry check.
- **Spirit of Redemption, Soulstone creation, pet re-summon after resurrection** belong to the spell, warlock/priest and pet lanes.
- **Resurrection sickness** uses spell 15007 for every race (vmangos reads `ChrRaces.resSicknessSpellId`); its -75% stat auras (79, 80, 101)
  have no handlers yet.

## Not a 1.12 packet

`SMSG_DEATH_RELEASE_LOC` exists only for 2.4.3 and 3.3.5 (wow_messages `smsg_death_release_loc.wowm`) and nowhere in vmangos; it is
deliberately not implemented.

## Where to look

| Area | Files |
|---|---|
| Seams, options | `Death/DeathSeams.cs`, `Death/DeathOptions.cs`, `Combat/MapCombat.Death.cs`, `Combat/MapCombat.DeathEffects.cs` |
| Data | `Kernel/WorldData/GraveyardData.cs`, `Data/Graveyards/*`, `Data/Content/Import/Mappers/GraveyardDumpImporter.cs` |
| Selection, repop | `Game/Graveyards/*`, `World/Graveyards/GraveyardFeature.cs` |
| Ghost | `Game/Death/Ghost/GhostForm.cs`, `Game/Spells/Auras/GhostAuras.cs`, `Game/Death/Visibility/GhostVisibilityRule.cs`, `World/Death/Ghost*Feature.cs` |
| Resurrection | `Game/Death/Resurrection/*`, `Game/Spells/Effects/Resurrect*.cs`, `Game/Spells/Effects/SelfResurrectEffect.cs`, `World/Death/ResurrectionFeature.cs` |
| Travel | `Game/Death/Travel/*`, `World/Teleport/TeleportHandlers.cs` (one additive check) |
| GM | `World/Gm/Death/DeathGmCommands.cs` |
