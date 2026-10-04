# Locomotion: movement handshake, flags, falls, environment (wave 3, lane movement-environment)

Retail reference for every rule: vmangos (`D:\refs\vmangos`, primary), cross-checked with
mangos-classic and the gtker packet layouts (`D:\refs\wow_messages`). Nothing was copied; each rule
below cites the file and lines it was verified against. Everything is retail by default; the
`Locomotion` configuration section holds the (few) switches.

## Overview

| # | Slice | State |
|---|---|---|
| 1 | loco-foundation (pending-change ledger, ack timeout, observer seam, root ack) | delivered |
| 2 | flag-auras-and-authority (water walk, hover, feather fall, CorrectData, root flag) | delivered |
| 3 | environmental-damage | delivered |
| 4 | fall-damage | delivered |
| 5 | undermap-void (antiundermap2) | delivered |
| 6 | speed-rates-players (speed auras, force-speed handshake) | delivered |
| 7 | speed-rates-creatures | delivered |
| 8 | mount-aura | delivered |
| 9 | knockback and player pull | delivered |
| 10-12 | liquid-environment-flags, mirror-timers-hazards, water-breathing-auras | delivered together |
| - | movement-flag-tests (opt-in anticheat flag tests) | not delivered: opt-in hardening, default off in vmangos, not retail behaviour |
| - | collision-height-dbc | not delivered: needs the developer's client CreatureModelData / CreatureDisplayInfo files, which are not available to test against |

No schema, store or data module changed anywhere in the lane (nothing it keeps is persisted: speeds, falls, breath timers and pending acks are rebuilt at login, as in
vmangos), so the Characters/World schema versions did not move and the MariaDB/PostgreSQL provider rule is not triggered. The tests that need terrain write synthetic `.map`
tiles to a temporary directory; none touches a database.

## Open questions (need a real client, a capture or a developer decision)

* GUID width of `SMSG_FORCE_MOVE_ROOT` and the other 1.12 movement SMSGs: packed (vmangos, mangos-classic, mangoszero, what the base sends) versus the full GUID that gtker lists
  for the root and spline-root packets. Packed is kept everywhere; every ack-bearing slice needs a real-client check.
* Slime damage (`Locomotion:SlimeDamage`, off), breath and fatigue base duration (60 s) and the lava tick (605 to 610 every 2 s): reference defaults, not proven.
* Knock back vertical speed: vmangos divides the effect value as an integer, mangos-classic as a float (57 gives 5 or 5.7).
* An environmental death marks the player as a PvP death in vmangos (a self kill is its own tap); retail behaviour of the corpse type and reclaim delay is unknown.
* The ghost's +25% run and swim speed (Ghost aura 8326) waits for the death lane's `ApplyGhostForm`; the speed formula already honours the aura.
* WMO liquid (lava and slime inside buildings, canals), `LiquidType.dbc` remaps and real model collision heights depend on collision/client data lanes.
* Server-driven player movement (Charge, Leap, Blink-style effects) needs a player spline and pathfinding and is not designed here.
* Taxi: an aura mount is not removed when a flight starts (needs the spell system in `TaxiFlightSystem`).
## Slice 1: loco-foundation (delivered)

### What it does

* **Pending-movement-change ledger** (`src/ArcaneCore.Game/Locomotion/PendingMovementChanges.cs`,
  vmangos `Unit.cpp:6619-6930`). Every server-ordered change of a player-controlled unit is a pair:
  the order is sent with the player's movement counter (shared with teleports, as vmangos
  `GetMovementCounterAndInc`) and recorded; the state changes only when the matching client ack
  arrives (counter + apply flag + type must match, `FindPendingMovementFlagChange` /
  `FindPendingMovementRootChange`). An ack that matches nothing is ignored and counted
  (`LocomotionState.WrongAckCount`, vmangos `OnWrongAckData`); it is never punished here.
* **Ack timeout** (`MapLocomotion`, an `IMapUpdater` with `[DefaultMapUpdater(Order = 10)]`,
  vmangos `CheckPendingMovementChanges` `:6619-6665`). The oldest unacknowledged change is enforced
  after `Locomotion:PendingAckResponseTimeMs` (4000, vmangos `Movement.PendingAckResponseTime`,
  `World.cpp:985`), five times longer while the player is being teleported. A change that a newer
  change of the same type superseded is only dropped. Enforcing applies the state, counts
  `FailedAckCount` (`OnFailedToAckChange`) and tells everyone with `SMSG_SPLINE_MOVE_*`
  (`SendMovementFlagChangeToAll`, build above 1.9.4). A player leaving the map has its latest pending
  changes applied silently (`ResolvePendingMovementChanges(false, ...)`).
  Age is accumulated from the map tick (not wall time) so the timeout is deterministic.
* **Root ack on the ledger** (`ArcaneCore.World/Locomotion/RootAckHandler.cs`, replaces the ack
  registration in `MovementHandlers`; an opcode may only be registered once). `Player.SetRooted` now
  records its order in the ledger. A matching ack stores the client block, sets `MOVEFLAG_ROOT` in
  `Unit.Movement` and relays `MSG_MOVE_ROOT/UNROOT` (packed GUID + block) to observers. Before this
  change an ack was accepted whenever it agreed with `IsRooted`, whatever its counter, and the server
  never set the flag.
* **Client-movement observer seam** (`ClientMovementObservers.cs`): classes marked
  `[MovementObserver(Order = n)]` in `ArcaneCore.Game` are discovered by reflection (fail-closed),
  others are added per world with `MovementObservers.Register`. `BeforeApply` runs after validation
  and before the block is stored and sees the previously stored block (vmangos `HandleFall` /
  `UpdateFallInformationIfNeed`, `MovementHandler.cpp:333-344`) and may correct the incoming block;
  `AfterApply` runs after the position was stored (`HandleMoverRelocation`). A throwing observer is
  logged and the others still run. `MovementHandlers.ApplyObserved` is the single hook, shared by
  movement packets and the acks. The security lane's `MovementValidator` stays before it; this lane
  never validates structure.
* **Per-unit state without editing hot classes** (`LocomotionState`, `unit.Locomotion`,
  a `ConditionalWeakTable`): pending ledger, `AuraLedger` (what the locomotion aura modules record,
  because `SpellSystem` is a world-daemon feature that map code cannot reach), fall start height and
  the ack counters. Nothing is persisted (vmangos saves none of it), so **no schema or data module
  changes**: the MariaDB/PostgreSQL provider rule is not triggered by this lane.
* **Shared-file edits**: `Unit` gained `AddMovementFlags` / `RemoveMovementFlags`, and `Relocate`
  keeps the server-owned flags (Root, WaterWalking, Hover, SafeFall) instead of clearing them
  (vmangos only rewrites the position of `m_movementInfo`); `Player.SetRooted` calls the ledger;
  `MovementHandlers` lost the root ack and gained the hook. See `docs/integration/locomotion.md`.

### Options (`Locomotion` section)

| Key | Default | Source |
|---|---|---|
| `PendingAckResponseTimeMs` | 4000 | vmangos `Movement.PendingAckResponseTime`, `World.cpp:985` |
| `RateDamageFall` | 1.0 (negative resets to 1) | vmangos `Rate.Damage.Fall`, `World.cpp:533` (`setConfigPos`) |

Only options whose rule is delivered exist; keys for undelivered rules would be stubs.

### Limits and open points

* `Player.SetRooted` keeps today's behaviour of always sending the order (its callers, logout and
  stun, run in the world); the new flag primitives send nothing and set the flag directly while the
  player is not in a map (vmangos `Unit::SetWaterWalking`, `Unit.cpp:7239-7245`).
* vmangos kicks a client that acks a root without the Root flag in its block
  (`MovementHandler.cpp:719-731`, a 1.14-client workaround). ArcaneCore applies the flag from the
  order and does not kick.
* GUID width: wow_messages lists the `SMSG_FORCE_MOVE_ROOT` family and `SMSG_SPLINE_MOVE_ROOT` with a
  full GUID for 1.12; vmangos, mangos-classic and mangoszero send a packed GUID. Packed is kept; only
  a real-client capture settles it. The handshake therefore needs a real-client check before it can
  be called retail-verified.
* The client's movement block is applied with the same `ApplyClientMovement` as any movement;
  a later heartbeat without the Root flag still overwrites the stored flag until the flag-authority
  slice (not delivered in this wave if listed under "not done" in the report) lands.

## Slice 2: flag-auras-and-authority (delivered)

* **Water walk, hover, feather fall as server orders** (`MovementControl.Request`, vmangos
  `Unit::SetWaterWalking/SetHover/SetFeatherFall`, `Unit.cpp:7224-7296`,
  `MovementPacketSender::AddMovementFlagChangeToController`, `MovementPacketSender.cpp:306-440`):
  `SMSG_MOVE_WATER_WALK / LAND_WALK / SET_HOVER / UNSET_HOVER / FEATHER_FALL / NORMAL_FALL` = packed GUID +
  u32 counter. The server flag changes only on the client's `CMSG_MOVE_WATER_WALK_ACK /
  HOVER_ACK / FEATHER_FALL_ACK` (u64 GUID, u32 counter, movement block, u32 apply;
  `ArcaneCore.World/Locomotion/FlagAckHandlers.cs`, vmangos `HandleMovementFlagChangeToggleAck`
  `MovementHandler.cpp:536-644`), which must match a pending change; the match is relayed to the observers
  as `MSG_MOVE_WATER_WALK / HOVER / FEATHER_FALL` (packed GUID + block), never to the mover.
  A request that changes nothing is dropped; a player that is not in a map yet gets the flag directly and no packet
  (login restore, `Unit.cpp:7239-7245`).
* **Aura modules** (`Spells/Auras/MovementFlagAuras.cs`): `WATER_WALK` 104, `FEATHER_FALL` 105 and `HOVER` 106
  order the change on apply and the opposite on removal (vmangos `HandleAuraWaterWalk` etc.,
  `SpellAuras.cpp:2278-2303`; as in vmangos removing one aura clears the state even if another aura of the same type
  remains); `SAFE_FALL` 144 is only a value (vmangos `HandleAuraSafeFall`, `SpellAuras.cpp:209`, "implemented in
  WorldSession::HandleMovementOpcodes") that the fall rules read from the `AuraLedger`. Players only: creatures
  have no client to ask. The aura amounts (Safe Fall 1860 = 17 yards, Feline Grace 20719 = 17) are from the
  design survey of `D:\refs\classic-db` and were not re-read by this slice.
* **Flag authority** (`Locomotion/Flags/FlagAuthorityObserver.cs`, a `Before` observer of order 10):
  `MovementInfo.CorrectData` (`Object.cpp:153-189`: Root with any moving flag drops Root; turn left+right,
  strafe left+right, pitch up+down, forward+backward drop both; the hover rule is commented out in vmangos and is
  not applied) and the rule that the client cannot clear the Root flag of a unit the server has rooted
  (`HandleMoverRelocation`, `MovementHandler.cpp:1067-1071`). Every other flag stays the client's own.
* **Ghost water walk** now goes through the handshake (`MapCombat.SetGhost` and the login restore call
  `MovementControl.Order`), so the ghost's `WaterWalking` flag follows the ack. The ghost aura (8326: aura 95,
  run +25%, swim +25%) is still not cast: `CombatHooks.ApplyGhostForm` is the death lane's seam and a no-op on the
  base, so the ghost speed bonus waits for it (and for the speed slice, not delivered).

Limits: login restore of `PlayerFlags.Ghost` without the aura sends the order unconditionally (kept from the base);
the 1.12 client's acceptance of the packed-GUID layouts is unverified (see slice 1). The stricter flag tests of
vmangos' anticheat (`MovementAnticheat.cpp:750-870`) are not delivered (opt-in hardening, default off in vmangos).
## Slice 3: environmental-damage (delivered)

`EnvironmentalDamage.Apply(world, player, type, damage)` (`Locomotion/Hazards/EnvironmentalDamage.cs`) is vmangos
`Player::EnvironmentalDamage` (`Player.cpp:713-775`). It is the primitive the fall observer, the void check and the
(not delivered) breath/fatigue/lava pulses call; nothing else in this lane deals environmental damage yet.

* A dead player or a game master takes nothing and gets no packet.
* Immunity per school returns 0 without a packet: fire for lava and fire, nature for slime, physical for exhausted,
  drowning and fall (`:719-758`). Only fire/lava and slime ask for absorb and resist; fall, drowning and fatigue are
  never absorbed (client 1.7.0 and later). Damage becomes `damage + (resist < 0 ? |resist| : 0) - (resist > 0 ? absorb +
  resist : absorb)`, floored at 0, exactly as vmangos writes it (`:752-755`).
* `SMSG_ENVIRONMENTAL_DAMAGE_LOG` = u64 GUID, u8 type, u32 damage, u32 absorb, i32 resist (vmangos Combat.cpp:105-127,
  gtker smsg_environmentaldamagelog.wowm) to the victim and its observers; `FALL_TO_VOID` (6) is logged as `FALL`
  (`Unit.cpp:5147-5160`).
* The damage is `MapCombat.DealDamage(player, player, ...)` (self damage: no combat, no threat). A player who dies loses
  10% durability on worn items (`DurabilityLossAll(0.10, false)`) and receives the empty `SMSG_DURABILITY_DAMAGE_DEATH`
  (`:760-766`, "confirmed on classic that dying from lava, fatigue and drowning causes durability loss").
* vmangos quirk kept: a self kill has the player as its own tap, so `SetPvPDeath(true)` (`Unit.cpp:1180`); the base's
  `MapCombat.Kill` already does the same. Whether retail marks an environmental death as PvP (corpse type, reclaim
  delay) is not provable from the references: open question.

**Limit (seam):** absorb, resist and immunity come from the spell combat rules (`IsImmuneToDamage`,
`CalculateDamageAbsorbAndResist`), which are not on this branch. `IEnvironmentalDamageMitigation` is the seam
(`LocomotionEnvironment.RegisterMitigation`); the default mitigates nothing, so lava is not reduced by fire resistance
and fire immunity does not protect until that lane registers an implementation. `DealDamageMods` (`:763`) is not
ported: it only changes damage for GM-like invulnerability states and game masters are excluded already.
## Slice 4: fall-damage (delivered)

`FallObserver` (`Locomotion/Falling/FallObserver.cs`, `[MovementObserver(Order = 20)]`, a `Before` observer because
`HandleFall` reads the previously stored flags) ports `Player::UpdateFallInformationIfNeed` and `Player::HandleFall`
(vmangos `Player.cpp:20799-20870`, called from `HandleMovementOpcodes`, `MovementHandler.cpp:333-344`).

* **Fall start** (`m_fallStartZ`, `LocomotionState.FallStartZ`): a block with Jumping or FallingFar records the height
  (again when the player rises above it); `MSG_MOVE_FALL_LAND`, `MSG_MOVE_START_SWIM`, a Hover or SafeFall flag, or a
  block with neither Jumping nor FallingFar forgets it (`:20799-20817`). A teleport, spell relocation, taxi stop or
  login does too (`Unit.Relocate` resets it; vmangos `SetFallInformation(0)` at `Player.cpp:1932,2082,15051`).
* **Landing damage** on `MSG_MOVE_FALL_LAND` (not while taxi flying): the previously stored block must have FallingFar,
  the reported fall time must be at least 1229 ms (`:20832`), the landing must not be above the start height, and the
  distance at least 14.57 yards (`:20845`); not a dead player, a game master, or a unit with a Hover (106) or Feather Fall
  (105) aura. Safe Fall (144) amounts are summed and subtracted from the distance; damage is
  `(uint)((0.018f * (zDiff - safeFall) - 0.2426f) * maxHealth * Locomotion:RateDamageFall * takenMod)` capped at the
  maximum health (`:20850-20867`), float arithmetic and truncation as in vmangos, dealt as `EnvironmentalDamage` FALL
  (slice 3: log, self damage, durability and death consequences). The 14.57 gate, not the formula's own zero (13.48
  yards), is what first yields damage. Worked values are in `FallDamageTests` (z 14.56 -> 0, 14.57 at 1000 hp -> 19,
  30 at 2000 hp -> 594, 40 at 3000 hp -> 1432, 75 at 3000 hp -> 3000 capped).
* **Seam:** `takenMod` (the physical damage-taken-percent auras, `:20854`) belongs to the spell combat rules and is 1 until
  `LocomotionEnvironment.RegisterFallModifiers` is used (`IFallDamageModifiers`). Safe Fall, Hover and Feather Fall are
  read from the aura ledger, which `MovementFlagAuras` (slice 2) fills; any other lane's aura of those types is not seen
  unless it records itself in the ledger.
* **Not delivered:** `SetJumpInitialSpeed` (extrapolation only); the knockback "launched" reset and the knockback-ack fall
  reset (no knockback in this wave); transports do not exist, so the transport branches compare the block's transport
  fields but never meet a transport. Real clients report fall time and z in their own way: damage numbers were checked
  against the formula, not against a 1.12.1 client.
## Slice 5: undermap-void (delivered)

`UndermapObserver` (`Locomotion/Falling/UndermapObserver.cs`, `[MovementObserver(Order = 40)]`, an `After` observer because it
reads the stored position) ports "Antiundermap2" (vmangos `HandleMoverRelocation`, `MovementHandler.cpp:1132-1161`): a
non-GM player below z = -500 takes `FALL_TO_VOID` damage of half the current health (the whole health on a battleground
map, `MapTemplate.IsBattleground`), logged as a fall. If that kills, `KillPlayer` and `RepopPlayer` run (vmangos
`KillPlayer` + `BuildPlayerRepop`; this core's `RepopPlayer` already schedules the ghost's graveyard trip, so the graveyard hook
is not called a second time for the death). A player that is already a ghost only takes `Hooks.RepopAtGraveyard` again, once per packet as
vmangos does ("this is actually called many times while falling"). A 1-health player takes 0 damage (1 / 2 = 0) and is
still sent to the graveyard. `RepopAtGraveyard` is the death lane's hook: it asks the graveyard feature's `IGraveyardRepop`
(docs/areas/graveyards-resurrection.md) and does nothing without graveyard data.

Not delivered: antiundermap1 (`UndermapRecall`, `:1105-1127`: more than 100 yards below the ground height while
falling, return to the last safe position, Warsong Gulch below z = 250). It needs a safe-position record and the ground
height under the player from the collision and pathfinding lanes.
## Slice 6: speed-rates-players (delivered)

The speed auras change a player's speeds through the order/ack handshake (vmangos `Unit::UpdateSpeed`,
`Unit.cpp:6959-7100`; `SetSpeedRate`, `:7152-7183`; the aura handlers `SpellAuras.cpp:3972-4040`; the ack handler
`MovementHandler.cpp:415-534`). 547 classic spells carry one of these auras (Sprint, Aspect of the Cheetah, Ghost Wolf, every
snare, mounts), none of which did anything before.

* **Formula** (`Locomotion/Speed/UnitSpeed.cs`, `ComputeRate`): run, unmounted: `bonus * (100 + main) / 100` with
  `main` = strongest MOD_INCREASE_SPEED (31), `bonus` = the larger of the product of MOD_SPEED_ALWAYS (129) and
  `(100 + strongest MOD_SPEED_NOT_STACK (171)) / 100`; mounted (a mount display is set) the same with 32, 130, 172, so
  Ghost Wolf does not apply on a mount. Swim: strongest MOD_INCREASE_SWIM_SPEED (58). USE_NORMAL_MOVEMENT_SPEED (191) caps run
  and swim at `amount / base`. Run, run-back and swim are then multiplied by `(100 + strongest MOD_DECREASE_SPEED (33)) / 100`.
  Swim-back is never updated. Worked values (`SpeedRateFormulaTests`): Ghost Wolf +40 = 9.8; +40 with a -50 snare = 4.9;
  mounted +60 = 11.2; mounted +60 with +25 always = 14.0; swim +50 = 7.083333.
* **Handshake** (`UnitSpeed.SetRate`): a player in a map is sent `SMSG_FORCE_{WALK,RUN,RUN_BACK,SWIM,SWIM_BACK}_SPEED_CHANGE` =
  packed GUID, u32 counter, f32 speed (byte-exact against gtker `smsg_force_run_speed_change.wowm`) and the change is
  recorded; the speed changes on the matching `CMSG_FORCE_*_SPEED_CHANGE_ACK` (u64 GUID, counter, movement block, f32 speed;
  counter, type and speed within 0.01 must match; parsed against the gtker test vector) and the client-reported speed is
  stored before the block is. The observers get `MSG_MOVE_SET_*_SPEED` (packed GUID, block, speed), not the mover. An
  unacknowledged change is enforced after `Locomotion:PendingAckResponseTimeMs` with `SMSG_SPLINE_SET_*_SPEED` to everyone; a
  superseded one is dropped. A player that is not in a map (login restore) is set directly with no packet (explanation (1),
  `Unit.cpp:7167-7173`); a non-player unit changes at once and everyone gets the spline packet.
* **Auras**: `Spells/Auras/SpeedAuras.cs` registers 31, 32, 33, 58, 129, 130, 171, 172, 191, each recording itself in the aura ledger
  and recomputing the speeds its vmangos handler does (33: run, run-back, swim; 191: run, swim; 58: swim; the rest: run).
* **Ghost rate**: `Locomotion:GhostRunSpeedWorld` / `GhostRunSpeedBattleground` (vmangos `Death.Ghost.RunSpeed.World` /
  `.Battleground`, `World.cpp:777-778`, default 1, range 0.1 to 10 as `setConfigMinMax`) multiply a player whose death state is
  CORPSE (read literally, `Unit.cpp:7040-7046`; at 1 it does nothing). The ghost's real +25% run and swim comes from the Ghost
  aura (8326), which `CombatHooks.ApplyGhostForm` (the death lane's seam, a no-op on this base) would have to cast: when it does,
  these auras take effect through the same formula with no further change.

Limits: creatures are covered by slice 7; pets and charmed units do not follow the owner's speed (`CallForAllControlledUnits`, no pets on
this base); the talent speed modifier on a caster's own speed aura (`SPELLMOD_SPEED`) is not applied; the turn rate is not
changed by any 1.12 aura and is not handled; a speed that is within 0.01 of the sent one is stored as the client reported it
(vmangos does the same). Unverified against a real 1.12.1 client (packed GUID widths, see slice 1).
## Slice 7: speed-rates-creatures (delivered)

Creatures take the same auras (the speed aura module acts on any unit) and `UnitSpeed.ComputeRate` adds the creature part of
`Unit::UpdateSpeed` (`Unit.cpp:7063-7094`): run and walk are multiplied by the template's `speed_run` (default 1.14286, vmangos
`DEFAULT_NPC_RUN_SPEED_RATE`) and `speed_walk` (default 1), and the run speed by 0.7, 0.6 or 0.5 while the creature is under
16%, 11% or 6% health (`SPEED_REDUCTION_HP_*`, `CreatureDefines.h:225-230`; health thresholds `Creature.cpp:971-973`), except
pets, world bosses and templates with `StaticFlags2 & 0x40` (`CREATURE_STATIC_FLAG_2_NO_WOUNDED_SLOWDOWN`, `CreatureDefines.h:140`;
155 of the 10384 classic template rows carry it, 901 have a non-default run rate, per the design survey of `D:\refs\classic-db`).
A creature is server-moved, so a change is immediate and everyone nearby gets `SMSG_SPLINE_SET_*_SPEED` (packed GUID + f32), no
handshake (`Unit::SetSpeedRate` third branch, `Unit.cpp:7176-7181`). `Creature.InitializeFields` now sets the template speeds
(vmangos `UpdateEntry` ends with `UpdateSpeed(MOVE_WALK/RUN)`), and `Creature.StartSpline` uses the live speed, so a snared wolf
takes twice as long to cover 14 yards (1750 ms to 3500 ms in `CreatureSpeedTests`).

Limits: vmangos refreshes the three health aura states every creature update but only recomputes the run speed when something
calls `UpdateSpeed` (a speed aura, fleeing at low health, returning from an assist call, `Creature.cpp:1191,1222,2282`); the
health used here is the one at the recompute, and the AI lane has to call `UnitSpeed.UpdateSpeed(creature, MoveType.Run)` at
those moments (no creature AI hooks were edited). Pets of players are normalised to the default run rate in vmangos; no pets
exist on this base, so that branch is not applied.
## Slice 8: mount-aura (delivered)

The follow-on mount cast and restriction rules are tracked in [mounts.md](mounts.md).

`SPELL_AURA_MOUNTED` (78, `Spells/Auras/MountAura.cs`, vmangos `HandleAuraMounted`, `SpellAuras.cpp:2251-2276`): the misc value is the creature
entry of the mount; its template's display becomes `UNIT_FIELD_MOUNTDISPLAYID` (the mount state, 1.12 has no mounted unit flag). Removing
the aura dismounts. 126 classic spells carry the aura (108 are item mounts).

* **Display source**: the creature data lives in the world daemon, so `IMountDisplaySource` (`LocomotionEnvironment.RegisterMountDisplays`) is
  implemented by `ArcaneCore.World/Locomotion/MountDisplayFeature.cs` from `CreatureWorldFeature.Content`: `Creature.ChooseDisplayId`, then the
  other-gender model half of the time, as a creature does at spawn. A creature entry that is not in the data is logged and the rider stays on foot
  (vmangos logs a database error and returns). Without a registered source nobody mounts.
* **Mount** (`MountService`, vmangos `Unit::Mount` `Unit.cpp:5794-5821` and `Player::Mount` `Player.cpp:18170-18230`): a player that is already mounted
  gets `SMSG_MOUNTRESULT` 2 (the new aura stays, the display does not change: vmangos does not remove it); a looting player gets 6 and the mount aura
  is removed; otherwise channels and auras that end on mounting (`AURA_INTERRUPT_MOUNT_CANCELS` 0x20000) are removed, the display is set and
  `SMSG_MOUNTRESULT` 10 is sent. **The result codes are vmangos / gtker's (`MOUNTRESULT_OK` = 10, `DISMOUNTRESULT_OK` = 3, `ALREADYMOUNTED` = 2,
  `LOOTING` = 6), not 0.** A creature updates its walk and run speed.
* **Dismount** (`Unit::Unmount`, `:5823-5842`, `Player::Unmount`, `:18233-18255`): auras that end on dismount (`AURA_INTERRUPT_DISMOUNT_CANCELS` 0x40)
  are removed, the display cleared, `SMSG_DISMOUNTRESULT` 3 sent; a unit that is not mounted is left alone and nothing is sent when it comes from an aura.
* **Speed**: the mounted speed auras (32, 130, 172) are slice 6 and apply only while mounted. A mount spell lists the mounted aura first, so the run speed
  order is sent when the 32 aura is applied right after (the same order vmangos relies on; `Unit::Mount` recomputes only for creatures).
* **New spell-system API** (`Spells/SpellSystem.InterruptFlags.cs`): `RemoveAurasWithInterruptFlags`, `InterruptChannelsWithFlags` (vmangos
  `RemoveAurasWithInterruptFlags`, `InterruptSpellsWithChannelFlags`) and `RemoveAurasByType` (`RemoveSpellsCausingAura`); `SpellAuraInterruptFlags`
  gained `DismountCancels` and `MountCancels` (vmangos `SpellDefines.h:583,594`).

Not delivered (the systems are not on this base): unsummoning or disabling the pet on mount and resummoning on dismount (`Player.cpp:18205-18218,18245-18250`),
the disallowed shapeshift form refusal (only for mounts without a spell), `ResetExtraAttacks`, the Silithyst drop on mounting. **Taxi**: `TaxiFlightSystem`
sets and clears the mount display itself and vmangos removes the Mounted aura when a flight starts (`ActivateTaxiPathTo`); here an aura mount is not removed
at taxi start, so the display is overwritten for the flight and cleared at its end while the aura stays: the taxi code needs the spell system to remove it.
## Slice 9: knockback (delivered)

`SPELL_EFFECT_KNOCK_BACK` (98, 133 classic spells; 51 are creature casts in dungeons and raids) and `SPELL_EFFECT_PLAYER_PULL` (124, 7 spells) in
`Spells/Effects/KnockbackEffects.cs`, after vmangos `Spell::EffectKnockBack` (`SpellEffects.cpp:5474-5484`) and `EffectPlayerPull` (`:5495-5505`);
the movement part is `Locomotion/Knockback/Knockback.cs` (vmangos `Unit::KnockBackFrom` / `KnockBack`, `Unit.cpp:9932-9960`).

* **Order**: a target that is stunned or rooted is left alone; its current non-melee cast is interrupted; a player in a map gets `SMSG_MOVE_KNOCK_BACK` =
  packed GUID, u32 counter, f32 vcos, f32 vsin, f32 horizontal speed, f32 vertical speed **negated** (gtker `smsg_move_knock_back.wowm`, vmangos
  `MovementPacketSender.cpp:241-278`) and a pending `KnockBack` change. The direction is the angle from the caster to the target in [0, 2 pi), or the target's
  orientation plus pi when it knocks itself back. Horizontal speed is `misc value / 10`; vertical speed is `value / 10` **with vmangos' integer division** of
  the effect value (57 gives 5; mangos-classic divides as a float and gets 5.7): vmangos is primary and is followed, the difference is an open question.
  A taxi passenger is skipped; the Dream Fog sleep (24778) is removed first. Only players are moved, as in every pre-WotLK core (mangos-classic
  `Unit.cpp:10816`: "Effect properly implemented only for players"); creatures are never knocked back, but their cast is still interrupted.
* **Pull**: toward the caster by `min(2D distance, value)` as a negative horizontal speed, `misc value / 10` vertical (vmangos itself flags this as "very wrong").
* **Ack** (`ArcaneCore.World/Locomotion/KnockbackAckHandler.cs`, vmangos `HandleMoveKnockBackAck`, `MovementHandler.cpp:747-802`): the movement block's jump
  section (cos, sin, xy speed, z speed) must repeat the order within 0.01 and the counter must match; a match ends the fall in progress, stores the block
  and relays `MSG_MOVE_KNOCK_BACK` (packed GUID, block, the four numbers) to the observers, not to the mover. Mismatches are ignored and counted.
  The server does not move the unit: the client's next movement packets carry the launch.
* **Timeout**: an unacknowledged knock back is not resendable, so after `Locomotion:PendingAckResponseTimeMs` it is dropped and counted as a failed ack, not
  enforced (`CheckPendingMovementChanges`, `Unit.cpp:6653-6658`).

Not delivered: `SetLaunched` / the anticheat's knock back tolerance and `SetJumpInitialSpeed` (extrapolation only); damage-immunity interactions (a spell
immunity that stops knock back belongs to the spell combat rules). Unverified against a 1.12.1 client (packed GUID width, see slice 1).
## Slices 10 to 12: liquid environment, mirror timers and hazards, water breathing (delivered together)

Players now know where they are in the liquids and the world hurts them accordingly: drowning, deep-sea fatigue and lava, with the client's breath bar.
Before this nobody could drown, tire or burn.

**Liquid environment flags** (`Locomotion/Hazards/LiquidEnvironment.cs`, `LocomotionState.Environment`; vmangos `Player::UpdateTerainEnvironmentFlags`,
`Player.cpp:20353-20420`). The terrain is asked at z + 0.01 with every liquid type whenever a player's position or map changed since the last time:
immediately after each accepted movement block (`LiquidFlagsObserver`, an After observer, vmangos `SetPosition`, `:5988`, with
`Movement.RelocationVmapsCheckDelay` = 0) and once per tick for logins, teleports and spell relocations (`MapEnvironment`). The flags
(`EnvironmentFlags`, vmangos `Player.h:75-86`): *Liquid* anywhere with liquid information; *Underwater* fully submerged (the liquid surface is above z + the
collision height); *InWater* water or ocean, in or under the surface; *InMagma* / *InSlime* also within 0.1 yard above the surface; *HighSea* a deep water cell at any
depth; *HighLiquid* deep enough to swim (surface above z + 0.75 x the collision height). No liquid clears them all. `SetEnvironmentFlags` side effects are ported:
entering or leaving swimmable water removes the auras and channels with the new `UnderWaterCancels` (0x80) / `AboveWaterCancels` (0x100) interrupt flags (through
`IEnvironmentSpellBridge`, implemented by `ArcaneCore.World/Locomotion/EnvironmentSpellBridgeFeature.cs` over the daemon's spell system), the high sea starts the fatigue
timer, submersion the breath timer (unless the player cannot lose breath), a hazardous liquid the lava timer.

**Mirror timers** (`MirrorTimer.cs`, a literal port of `MirrorTimer.cpp:19-150` with `ShortIntervalTimer`): a timer runs out while its scale is negative and pulses once on
expiry and then every 2000 ms; a positive scale regenerates at that many times real time and stops at zero. `MapEnvironment` (an `IMapUpdater`, order 10) runs
`UpdateMirrorTimers` for every player each tick (`Player.cpp:942-981`): activation (fatigue: high sea and not on a taxi; breath: submerged and able to lose breath; lava:
magma or slime), deactivation (no liquid, or dead; a ghost keeps its fatigue), durations (`MirrorTimer.Fatigue.Max` 60 s, `MirrorTimer.Breath.Max` 60 s times the breathing
multiplier, `MirrorTimer.Environmental.Max` 1 s), and `SMSG_START_MIRROR_TIMER` = u32 type, u32 remaining, u32 duration, i32 scale, u8 paused, u32 spell id /
`SMSG_STOP_MIRROR_TIMER` = u32 type (gtker `smsg_start_mirror_timer.wowm`; a pause is always a full resend, the vmangos client-UI workaround). The lava timer is never sent.
A game master's timers are frozen (`FreezeMirrorTimers`, `:2634,2656`; shown paused).
Pulses (`OnMirrorTimerExpirationPulse`, `:983-1062`), through the environmental damage service of slice 3: fatigue deals `maxHealth / 5 + urand(0, level - 1)` exhaustion
damage (a ghost is sent to the graveyard instead), breath the same as drowning, lava `urand(EnvironmentalDamage.Min, Max)` (605 to 610 by default, every two seconds).
Slime does nothing, as in vmangos and mangos-classic (both define `DAMAGE_SLIME` and never apply it); `Locomotion:SlimeDamage` (default false) is the explicit deviation that
applies the lava tick in slime. Whether retail hurt in slime is not provable from the references: open question. All numbers (60 s, 605 to 610) are the references' defaults and
are configurable; no capture proves them.

**Water breathing** (`Spells/Auras/WaterBreathingAuras.cs`, `SpellAuras.cpp:2305-2316`): `WATER_BREATHING` (82) makes the breath time zero (the bar never starts; while another such aura is
on the player a removal keeps it zero), `MOD_WATER_BREATHING` (155, Unending Breath) multiplies it by the product of (100 + amount) / 100. 22 classic spells carry the water auras.

Limits: the liquid kind is the one the `.map` file stores (no `LiquidType.dbc` remap, so liquid area spells such as the Undercity slime aura are not applied) and WMO liquid is not queried
(the terrain lane's `docs/integration/vmap-los.md`), so lava and water inside buildings are not seen; the collision height is vmangos' fallback of 2 yards for every race
(`LocomotionState.CollisionHeight`; the swim depth is three quarters of it), real model heights need the client's CreatureModelData; the feign death timer (hunter lane) and the Spirit
of Redemption form are not handled; the swimming mobs' threat table update on entering water (`updateThreatTables`) belongs to the creature AI. A pulse that kills is handled by the
next tick's combat update (the updater runs after combat so that combat stays every map's first updater).
