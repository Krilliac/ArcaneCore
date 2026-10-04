# Transform aura, Charge, Blink and the face-caster teleport (L8-transform-and-small-effects)

Target: WoW 1.12.1, build 5875. Reference evidence is the mangoszero server in `mangosserver/server/src/game/WorldHandlers` (the only server of that
generation in the reference set; the vmangos tree the other area docs cite was not available to this lane). The reference is evidence, not authority:
where ArcaneCore's design differs (world-thread affinity, collision services that may be absent, content in immutable stores) the difference is listed below.
No client behaviour was observed; everything that depends on it is under "Unverified".

## What is implemented

| Piece | Code | Reference |
| --- | --- | --- |
| `SPELL_AURA_TRANSFORM` (56): display change, restore, override rules | `Game/Spells/Auras/TransformAuras.cs` | `SpellAuraShapeshift.cpp:502-598` (`Aura::HandleAuraTransform`) |
| Creature entry -> display for the aura (world daemon seam) | `ITransformDisplaySource`, `TransformDisplays` (same file); `World/Spells/TransformDisplayFeature.cs` | `SpellAuraShapeshift.cpp:548-562`, `Creature::ChooseDisplayId` |
| `SPELL_EFFECT_CHARGE` (96) | `Game/Spells/Effects/ChargeEffects.cs`, `Game/Locomotion/Knockback/ForcedMovement.cs` | `SpellEffectObjectCombat.cpp:1245-1273`, `WorldObjectSummon.cpp:395-541` (`ContactPointNear`) |
| `SPELL_EFFECT_LEAP` (29, Blink) | same | `SpellEffectObjectCombat.cpp:944-1112` (`EffectLeapForward`) |
| `SPELL_EFFECT_TELEPORT_UNITS_FACE_CASTER` (43) | same | `SpellEffectSkillEnchantPet.cpp:413-436` |
| Cast checks of the three effects | `ChargeEffects.MovementEffectCastCheck` | `SpellChecks.cpp:1124-1131`, `:1380-1412` |
| Aura support matrix row 56 is now `Handler` | `Spells/Auras/Support/AuraSupportBaseline.cs` | |

The effect and aura modules are discovered by `SpellHandlerModules` (no registration line was needed in `SpellSystem.Effects.cs`); the display source is a world
feature, discovered like every `IWorldFeature`.

### Transform aura

* Misc value = creature entry. The display is the template's (`Creature.ChooseDisplayId`, weighted by the display probabilities; no gender swap, unlike the mount
  aura). An entry that is not in the creature data gives the pink pig, display `16358` ("pig pink ^_^"), and the feature logs the entry. With no display source
  registered every entry counts as unknown.
* Misc value 0 is defined for Orb of Deception (spell `16739`) only: the wearer's *native* display maps to the Orb model (16 race/gender rows, `10134-10149`,
  taken from the reference table). A native display with no row leaves the display alone. Any other misc-0 spell is reported through
  `ITransformDisplaySource.ReportNoModel` and changes nothing (the reference logs "need custom defined model").
* Removal resets the display to `NativeDisplayId`, then re-applies one of the transform auras still on the unit, a negative spell by preference, else the first
  (`GetAurasByType` order). So a second transform overrides the first, and the first comes back when the second ends; removing the older one while a newer one is on
  re-applies the newer one.
* The unit keeps an "active transform" record (`TransformAuras.ActiveHolder`, a static weak table keyed by unit, world thread only, like `TransformScale`). A new
  transform always sets the display; it takes the record only when none is set, when it is negative, or when the record is positive (a positive transform over a
  negative one changes the model without taking the record). The record is read by nothing else yet; it exists so the "negative wins" rule of the restore is the
  reference's, and for later consumers (`GetTransform`).
* The shapeshift service already skips a form's display while any Transform aura is on the unit (`ShapeshiftService.HasTransform`), and the polymorph regeneration
  rule in `CombatOptions` reads the same aura type.
* Cost: apply and removal only, never per tick.

### Charge

* The contact point is on the line from the target toward the caster at `3.666666 + both bounding radii` from the target (`ForcedMovement.ContactPoint`). Its floor is
  looked up with the collision service (`map.Collision.GetHeight`; the target's z when no height is known). A candidate is accepted when its floor is within reach
  of the target's z and the target can see it; the angle search walks `0, +-45, +-90, +-135, 180` degrees around the bearing and falls back to the first point
  ("BAD BAD NEWS" branch of the reference).
* The way is the navigation path when the map's pathfinder returns a complete one ending on the contact point (`ForcedMovement.ChargePath`, the reference's
  `generatePath = true, forceDestination = true`), else the straight line.
* One `SMSG_MONSTER_MOVE` (the creature spline layout, `CreatureMovePackets.BuildPath`, no run flag, no facing) goes to the caster's own client when it is a player
  and to everyone who sees it, duration = path length / 24 yd/s. A creature caster ends its own spline first; a creature target stops moving.
* The caster is relocated to the end of the path at once (see "Deviations").
* A negative spell starts the caster's melee attack on the target (`map.Combat.Attack`). The effect never grants rage or a stun itself: a warrior's Charge spell has
  other effects (a trigger spell, an energize) that run after it through the ordinary effect loop; the tests cover exactly that composition (rage for the caster, the
  stun on the target, both through the triggered spell).
* Cast check: a charge by a stunned, rooted or root-flagged caster fails with `ROOTED`.

### Blink (Leap) and face-caster teleport

* Leap moves the caster the effect's radius along its facing, in 2 yard steps (`ForcedMovement.LeapDestination`), by `ITeleportSink` (for a player a near teleport with
  `MSG_MOVE_TELEPORT_ACK`; the world's sink is the teleport service). Each step takes the floor (within 4 yards of the estimate), refuses a ground/water edge of
  more than 2 yards, follows the water surface (0.8 yard below it), is refused when a static model blocks the segment from 1.5 yards above the previous point, and when the slope exceeds 50
  degrees. The blink ends at the last step that passed. While falling with no floor in reach (client sends the jumping flag) the destination is straight ahead and two yards
  lower, pulled to the water surface or nearby ground and back from a model hit.
* Face-caster teleport: the target is teleported to the spell's destination, or in front of the caster by the effect radius (plus both radii), turned to face the caster; a
  taxi-flying target is skipped.
* Cast checks: Leap and face-caster teleport fail on a taxi (`NOT_ON_TAXI`) and on a transport (`NOT_ON_TRANSPORT`); the face-caster teleport also needs an unrooted
  caster (Blink itself is allowed while rooted: "Blink has leap first and then removing of auras with root effect").

## Options

None. The charge speed, contact gap, blink step, slope limit and liquid range are the reference's constants (`ForcedMovement`); nothing here is a server policy a
realm would tune, so no config key was added (the config catalog is untouched).

## Deviations from the reference (and why)

* **The server does not step the charger along the spline.** The reference moves the unit with its spline each tick; ArcaneCore has a creature spline only and no
  player spline. Here the charger is relocated to the destination at once and the spline is sent for the clients to animate. Consequence: a hit that lands during the
  flight is resolved at the destination, and the "being moved" state is not modelled. This is the "player spline" gap `locomotion.md` listed.
* **No data is open.** A blink with no terrain or model heights around the caster goes the whole way at the caster's z; the reference would find no height and stay. A blink
  that starts on known ground and steps onto unknown ground (a hole, the map edge) stops, as the reference does. Collision absence is never read as "blocked"
  (`docs/integration/vmap-los.md`).
* **No other-unit avoidance** in the contact point: the reference's `ObjectPosSelector` keeps units from standing on one spot; here two chargers can end on the same spot.
* **Water uses terrain liquid only** (`map.GetLiquidStatus`); WMO liquid is not queried by the map yet.
* **Transform equipment**: the reference reloads a creature target's equipment template (`LoadEquipment`) on a transform and restores it on removal. ArcaneCore has no
  creature equipment swap seam, so a transformed creature keeps its equipment. Players are unaffected.
* **Transform end under a shapeshift form** restores the native display, not the form's (mangoszero does the same); a druid polymorphed out of a form needs the form
  re-applied by the shapeshift service, which is not done on a transform end.
* The battleground rule of the face-caster/leap check (`TRY_AGAIN` before the battleground starts) belongs to the battleground area.

## Unverified (no client or reference evidence in this lane)

* How a 1.12.1 client reacts to a `SMSG_MONSTER_MOVE` for its own player character at charge speed: the reference sends it, the layout is the repo's verified creature
  spline layout, and the comment "client has strange issues with other move flags" is why no run flag is sent; whether the client then also needs
  `SMSG_SPLINE_MOVE_SET_WALK_MODE` (the creature path sends one when the walk flag changes) was not established.
* Whether the client sends movement heartbeats while the spline plays, and how the server should treat them (the server position is already the destination).
* The exact classic data of Charge/Blink (effect order, trigger spell ids, ranges, radii) was not checked: the repo carries no `Spell.dbc`, and the tests use synthetic spells
  with the effect shapes the lane describes. The Orb of Deception display ids are the reference's, not checked against a 1.12.1 client.
* `GetHeightInRange` search distances (3 yards at the origin, 4 per step, 10 while falling) are the reference's; their fit to this repo's `GetHeight` (which searches downward
  from z + 2) is reasoned, not measured on real terrain.

## Tests

`tests/ArcaneCore.Game.Tests/Spells/TransformAuraTests.cs` (display set and restored, expiry, second transform overriding, restore order, positive over negative, pig for an
unknown entry or no source, Orb of Deception table and misc-0 report), `ChargeEffectTests.cs` (adjacency with and without radii, spline packet fields, rage and stun through the
triggered spell, attack start, positive charge, self target, navigation path and incomplete path, wall at the contact point, creature target and creature caster, rooted caster, blink
distance, facing, wall stop, slope, edge of ground, falling, taxi and transport refusal, face-caster teleport to a radius point and to a destination),
`tests/ArcaneCore.World.Tests/Spells/TransformDisplayFeatureTests.cs` (the daemon source reads creature data and registers itself).

Generated reference docs and the config catalog were not regenerated (the orchestrator does that after the merge); this lane adds no option and no migration.
