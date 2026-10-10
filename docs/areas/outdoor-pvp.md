# Area: Outdoor PvP (zone scripts, capture points, Eastern Plaguelands towers, Silithyst)

Branch `grok/w18-outdoor-pvp` (wave 18, based on `integrate/wave17` 857eb9dc). Reference: vmangos `src/game/OutdoorPvP/`
(`OutdoorPvPEP.*`, `OutdoorPvPSI.*`) and `src/game/Maps/ZoneScript.cpp` (the `OutdoorPvP` / `OPvPCapturePoint` base), cross-checked
with MaNGOS Zero `src/game/OutdoorPvP/` (ids, spells, world states, the honor value noted under gaps). Re-implemented, not copied.

## Delivered

| Piece | What it does | Reference |
|---|---|---|
| `Game/OutdoorPvP/OutdoorPvPZoneScript` | A zone script: its zones, the players by team, its capture points, `SendUpdateWorldState` to the zone, `TeamApplyBuff`, area triggers and flag drops; the capture points run once more than 1000 ms have passed, with the whole elapsed time. | `ZoneScript.cpp:278-299, 588-649`; `ZoneScriptMgr.cpp:116-127` |
| `Game/OutdoorPvP/CapturePoint` | The slider: players within the template radius who are outdoor-PvP active join (one objective at a time), the others leave with the slider hidden; it moves by (alliance - horde) x diff / 1000, capped at max / minTime per ms; states neutral / contested / progressing / full; the position percent is `ceil((v + max) / 2max x 100)`; position updates only when it moved or the difference just became 0. Template data 0, 2, 3, 12, 13, 16, 17 of the type-29 object; without one the fallback is max 1200, grey band 20%, radius 60, minTime 60 (the radius and minTime are not sourced). | `ZoneScript.cpp:32-72, 145-187, 301-454`; `GameObjectDefines.h:488-511` |
| `Game/OutdoorPvP/EasternPlaguelands*` | The four towers with their sniffed capture point, banner, flare and buffer positions; per state the art kit, sounds, flares, tower buffer (pulses 30882 while progressing), the tower count world states (2327/2328), Echoes of Lordaeron rank by count for Eastern Plaguelands, Stratholme and Scholomance, and the "all four towers" announcement. Rewards: Eastwall's Lordaeron squad, Northpass's curing shrine, Crown Guard's graveyard 927 (zone 139 and The Fungal Vale) with the banner aura and the Spirit of Victory, Plaguewood's flight master (faction 774/775). Leaving full control zeroes the tower's count. | `OutdoorPvPEP.cpp`, `OutdoorPvPEP.h`, `WorldStates.h:119-160` |
| `Game/OutdoorPvP/Silithus` | Silithyst turn-ins at triggers 4162/4168 with the flag (29519): +1, announcer yells at 25/50/75/100%, dust bags per 15 (positions from vmangos migration 20241228161610), quest credit 17090/18199, Traces of Silithyst, 199 honor and the Cenarion reward; at the maximum (200) Cenarion Favor for the team (removed from the other), the zone text and both counters reset. The last controller gets the favor on entering. Mounting or stealthing away from the own trigger casts the drop spell 29533. | `OutdoorPvPSI.cpp:39-282`, `Unit.cpp:5802-5810`, `SpellAuras.cpp:3637-3645` |
| `World/OutdoorPvP/OutdoorPvPFeature` | One script per continent map (instance 0): zone presence from the zone listener and logout, SMSG_INIT_WORLD_STATES provider, area triggers, the flag drop from mount/stealth auras, `Player::IsOutdoorPvPActive`, the Crown Guard graveyard as an `IGraveyardLinkSource`. | `Player.cpp:2215, 6596-6598, 6720-6724` |
| `Game/Graveyards/IGraveyardLinkSource` | Run-time graveyard links (vmangos `AddGraveYardLink(..., persist=false)`): the repop service keeps the closest of its choice and the extra links on the spirit's map. | `ObjectMgr::AddGraveYardLink` |

Moving between zones of one script (Eastern Plaguelands into Stratholme) keeps the player in the script; vmangos leaves and re-enters,
which ends in the same buffs and states.

## Gaps

- Capture-point template values come from the loaded `gameobject_template` (type 29); without them the fallback above is used.
- The Silithyst maximum is not persisted (vmangos keeps it as a saved variable); it is the default 200 after every restart.
- Eastwall squad formation, the Spirit of Victory's waypoint path and the curing shrine's spawned-by-default flag are not modelled.
- MaNGOS Zero awards 189 honor per tower capture; vmangos leaves the credit to the buffer's 30882 pulse, which is followed here.
- Silithus's `SetSilithusPVPEventCompleted` game-event hook is commented out in vmangos and not implemented.
