# Unit control: charm, possession, mana burn, funnels and Spirit of Redemption

Wave-2 lane "unit-control" (branch `claude/w2-unit-control`, based on the proc-engine tip). Every rule cites the vmangos source it was
checked against (`D:\refs\vmangos`, GPL, read-only); spell shapes in the tests are reduced classic-db rows.

## Charm and possession (`Game/Pets/Control`)

| Piece | What it does | vmangos |
|---|---|---|
| `CharmService` | the four control aura handlers, `Uncharm` / `RemoveCharmAuras`, the control primitives (`SetCharm`, `SetCharmerGuid`, `SetMover`, `SetView` on PLAYER_FARSIGHT, `UpdateControl` + SMSG_CLIENT_CONTROL_UPDATE, `RestoreFaction`, `InitCharmInfo` / `ClearCharmInfo`, the possess and charm bars), PetAI switch and AI rebuild, the release threat | `Unit::ModPossess`, `Aura::HandleModCharm`, `Player::ModPossessPet`, `HandleAuraAoeCharm` (SpellAuras.cpp:2954-3442, 5740-5748); `Unit::Uncharm`, `RemoveCharmAuras`, `SetCharm`, `RestoreFaction`, `UpdateControl`, `RemoveAttackersThreat` (Unit.cpp:4868-4958, 10802-10917); `Player::PossessSpellInitialize`, `CharmSpellInitialize`, `RemovePetActionBar`, `SetClientControl`, `GetConfirmedMover` (Player.cpp:17400-17511, 20123-20149); `CharmInfo::InitPossessCreateSpells` / `InitCharmCreateSpells` (Unit.cpp:8362-8455) |
| `CharmCastCheck` | possess only by a player (BAD_TARGETS); charm/possess target already charmed (CHARMED), above the effect value in level (HIGHLEVEL); a pet must be dismissed first (ALREADY_HAVE_SUMMON, or dismissed with DISMISS_PET_FIRST); a second charm (ALREADY_HAVE_CHARM, or uncharmed first); a charmed caster (CHARMED); possess pet needs a player, a pet (NO_PET), no other charm, a pet nobody else charms | Spell.cpp:6292-6363 (build 5875 answers CHARMED, not FIZZLE) |
| `MapUnitControl` (default map updater, order 160) | the links of one map; releases the target when the controller leaves the map, gives the controller its body back when the controlled unit leaves, ends both on logout / far teleport (`OnPlayerRemoved`), runs `PlayerControlledAI` for a charmed player, and runs delayed unit actions (`Schedule`) | `Unit::RemoveFromWorld` → `Uncharm` (Unit.cpp:8276-8282), `Player::TeleportTo` → `RemoveCharmAuras` (Player.cpp:1819, 2061), AI/PlayerAI.cpp:173-330, `m_Events.AddLambdaEventAtOffset` |
| `UnitControl` | the possessor GUID, UNIT_STATE_POSSESSED, a non-pet's charm info, the player's mover and client mover, the invincibility threshold, the camera view point | Unit.h:480-508, 1269-1270; Player.h:1730-1733; Camera.cpp |
| `CharmPackets` | SMSG_CLIENT_CONTROL_UPDATE (packed GUID, u8), the possess and charm SMSG_PET_SPELLS forms | Packets/Misc.cpp:994-1004; Player.cpp:17400-17505 |

Behaviour, in the order vmangos runs it:

* **Possess** (aura 2, Mind Control): the possess-summon dummies (126, 6272, 11403) go; the target is possessed (state and UNIT_FLAG_POSSESSED,
  charmer and possessor GUIDs) and takes the caster's faction; the caster's camera (PLAYER_FARSIGHT), UNIT_FIELD_CHARM and mover move to it; the
  target stops fighting (combat, threat list, hostile references); it gets an empty possess bar (attack, nine passive slots, its creature spells
  as passive buttons except charm spells, passive ones cast on itself), react passive, command stay; the caster gets SMSG_PET_SPELLS with the aura's
  remaining duration; a creature takes PetAI; both clients are told who moves what; the target stops; the caster gets the target's OWNER_ONLY
  fields. On the end: threat on the target moves to the caster (`RemoveAttackersThreat`), the caster gets mover, charm field, control
  (SMSG_CLIENT_CONTROL_UPDATE for itself, then "not the target"), its own camera and an empty bar; unless the aura is deleted the target is
  released (flags, GUIDs, faction, combat stop, control, stop, charm info), a creature rebuilds its AI and, unless the possession ended by its
  death, attacks its former master with threat equal to its maximum health; Razuvious' understudies (16803) cast Mind Exhaustion (29051).
* **Charm** (aura 6, Enslave Demon, Dominate Mind; aura 177 for Chains of Kel'Thuzad only): the target loses every other charm/possess/AoE charm
  aura, takes the charmer and its faction, stops fighting, gets the pet bar (defensive, follow, returning), interrupts its casts; a creature takes
  PetAI; a warlock's demon gets a pet number, a name timestamp and, with class 0, class mage; a charmed player is driven by `PlayerControlledAI`;
  a player charmer flags the target PLAYER_CONTROLLED, gets SMSG_PET_SPELLS (the charm spell words for a warlock's demon) and the target follows it.
  The end restores the faction (a pet its owner's), clears the warlock pet number, gives the caster the threat and an empty bar, interrupts casts,
  restores a player's race faction; a player whose living charmer is in combat stays in combat with it; a creature rebuilds its AI and attacks
  its former charmer (not after its own death).
* **Possess pet** (aura 128, Eyes of the Beast): camera, charm field and mover go to the pet, which is possessed and stops; on the end the player
  gets them back and the pet loses its charmer, but keeps the possessed state until the client names its own mover again
  (CMSG_SET_ACTIVE_MOVER); a pet farther than the visibility distance (vmangos: the grid activation distance) is then saved and dismissed
  (`RemovePet(PET_SAVE_REAGENTS)`, stored as PET_SAVE_NOT_IN_SLOT). The 120 yd leash saves a hunter's pet the same way before it goes.
* **Ends**: the aura's end (expiry, dispel, damage break, channel end, cancel), `COMMAND_DISMISS` on a charmed creature that is not a pet,
  guardian or mini pet (`pCharmer->Uncharm()`, review finding 32; a charmed or possessed Pet follows the Pet branch, so a hunter's pet, judged by
  its own owner and not by whoever controls it, is left alone as in vmangos Unit.cpp:8758-8769), `CMSG_PET_ABANDON` on the player's charm when it is the owner (as vmangos), a dying charmer (`Player::SetDeathState` →
  `Uncharm`; done for a creature charmer too), the charmed unit's death (its auras go), a pet unsummoned while charmed or possessed
  (`Pet::Unsummon`), the controller or the controlled unit leaving the map.
* **Pet opcodes** accept the player's charm (`GetCharm`) besides its pet; a charmed player only takes commands and reactions (melee attack,
  stay/follow flags, dismiss). Stay and follow do not move a possessed creature. CMSG_CANCEL_AURA lets a possessor cancel its own possess or
  possess-pet spell (the "except own aura spells" branch); CMSG_CANCEL_CAST and CMSG_CANCEL_CHANNELLING are ignored while the player moves
  another player.
* **Movement**: a client's MSG_MOVE_* moves `GetConfirmedMover()`: the possessed unit once the client switched to it, the player itself until then
  (vmangos: "allowed to move self"), nothing while the player is possessed or charmed. A possessed creature is relocated (never while the server
  moves it on a spline) and the block is relayed to its observers except the possessor, a possessed player through its own locomotion observers
  and to its own client. CMSG_SET_ACTIVE_MOVER and CMSG_MOVE_NOT_ACTIVE_MOVER follow vmangos (the latter ignores the block while the moved
  player, not the sender, is being teleported). The creature system runs only the AI of a possessed
  creature (no leash, crowd-control movement or generator), and PetAI skips return movement and autocast while it is possessed.
* **Camera**: the map evaluates a player's visibility from its view point (the possessed unit), whose own body is always in range; a view point
  that moves flags its viewer for a visibility pass.
* **Fear and confuse** on a controlled unit (or its controller) re-send the control state (`Unit::SetFeared` / `SetConfused` → `UpdateControl`).
* **Charmer defence**: a charmed creature hears its charmer's fights (PetAI `OwnerAttacked` / `OwnerAttackedBy`) through its map's damage event.
  `MapUnitControl` subscribes it once per map, so the world-wide `CharmService` holds no map and an unloaded instance map can be collected.

## Mana burn and funnels (`Spells/SpellSystem.PowerBurn.cs`, `PowerBurnModule`)

* SPELL_EFFECT_POWER_BURN (the priest's Mana Burn): a target of the effect's power type loses up to the value; the burn times EffectMultipleValue
  (through SPELLMOD_MULTIPLE_VALUE) is school damage (bonus, crit, resist, absorb, log). vmangos Spell::EffectPowerBurn (SpellEffects.cpp:1778-1809).
* SPELL_AURA_POWER_BURN_MANA (Ignite Mana 19659, Soul Tap 24619, Brood Affliction: Blue 23153): each tick burns rand_dither(amount) of the power,
  the burn times the multiple (dithered) is spell damage with the caster's bonus and crit (rolled on every tick, even when nothing was burned, as
  vmangos does), resist and absorb, a periodic SMSG_SPELLNONMELEEDAMAGELOG,
  periodic procs, then the damage; an immune target is reported. Aura::PeriodicTick (SpellAuras.cpp:6300-6352).
* SPELL_AURA_PERIODIC_HEALTH_FUNNEL (Blood Siphon 24322, Blood Funnel 24617): the PERIODIC_LEECH tick (`DrainAuras.TickLeech`), with the leech's
  DoT snapshot of the amount (HandlePeriodicHealthFunnel). The 1.12 warlock Health Funnel is PERIODIC_HEAL plus a per-second health cost
  (docs/areas/warlock-mage-utility.md, wlm-04), not this aura.
* SPELL_AURA_PERIODIC_MANA_FUNNEL (63) stays unhandled: vmangos maps it to HandleUnused and the only row is "zzOLDMana Funnel" (1941).

## Spirit of Redemption (`Spells/SpellSystem.SpiritOfRedemption.cs`, `SpiritOfRedemptionModule`)

* `MapCombat.Kill`: a priest holding the talent (20711) whose killing blow is not the spirit's own Suicide (27965) does not die: casts are
  interrupted, the self-resurrection spell is kept across the death aura removal, and 27827 is cast with the maximum health as its heal. The rest of
  the kill happens (credit and honor, threat references, PvP death, durability, `UnitKilled`; the battleground already counts no death while 27827
  is on). vmangos Unit::Kill (Unit.cpp:1108-1140). The PvP death is remembered at this first death only (Unit.cpp:1177-1180).
* `MapCombat.DealDamage`: a unit with an invincibility threshold never dies of damage, and every hit, lethal or not, takes at most the health
  above the threshold (`min(health - threshold, damage)`, nothing at or below it); threat, rage and the returned damage keep the full amount
  (Unit.cpp:825-850). The spirit (threshold = maximum health, at full health) therefore loses nothing to any hit.
* 27827's form 32 (`ShapeshiftService`): display 16031, linked spells 27792 then 27795 (SpellAuras.cpp:2405-2407, 5480-5483).
* SPELL_AURA_SPIRIT_OF_REDEMPTION (27795): stand up, full mana, invincible at the maximum health; Untransform Hero (25100) a batching interval
  (400 ms) later. When the form ends (27827 lasts 10 s) the aura goes: the spirit is stunned and, a batching interval later, unstunned, made mortal
  and killed by Suicide. Aura::HandleSpiritOfRedemption (SpellAuras.cpp:5699-5738).

## Limits (recorded, not hidden)

* **Data**: the creature spell slots a charm or possession puts on the bar (vmangos `Creature::m_spells` from `creature_spells` /
  `creature_template.spell_list_id`) are not in the repository: `CharmService.CreatureSpells` is the seam and defaults to none, so a charmed or
  possessed creature's bar holds the commands only. A charmed demon's spells therefore need that data.
* A charmed **player** is not moved by the server (vmangos MoveChase/MoveFollow: players have no server movement generator here) and casts no
  spells (PlayerControlledAI's usable-spell list); it melees what its controller fights. The group update broadcast at the end of a charm on a
  player and the loot release when a looting player is charmed are not done.
* Script targets (`IsScriptTarget`) are not modelled in the charm cast check, so every charm effect with a unit target is level-checked.
* The pre-1.10 delayed possession (a target that itself possesses something) is not used by build 5875 and not ported.
* A rooted possessed creature does not get the root re-sent to its controller (`AddMovementFlagChangeToController`), and the creature's walk mode
  is not copied from the possessor.
* The creature PvP flag reset at the end of a charm (`CREATURE_STATIC_FLAG_PVP_ENABLING`) is not modelled (no static PvP flag on this base).
* Logging out as the spirit does not kill the player at once (vmangos WorldSession.cpp:706-712); the spirit simply ends with the logout.
* The SoR follow-up steps run in the map update after the interval (`MapUnitControl.Schedule`), so they need the unit's map; outside a map they
  run at once.

## Tests

* `tests/ArcaneCore.Game.Tests/Pets/CharmPossessTests.cs` (16; RED on the proc-engine base: 13 of the first 15 failed, the two characterization
  cases passed; the possessed hunter's pet dismiss case failed against the first version of this lane), `UnitControlTests.cs` (11: confirmed
  mover, move-not-active-mover and its teleport check, camera visibility, Eyes of the Beast return, dismissal and save, the leash save, a player
  charmed by a creature, the possessor's cancel, fear on a possessed creature, the per-map damage relay; the cancel case was proven load-bearing
  by reverting `SpellSystem.CancelAura.cs`).
* `tests/ArcaneCore.Game.Tests/Spells/PowerBurnTests.cs` (6; RED: 4 failed, then the every-tick crit roll failed against the first version) and
  `SpiritOfRedemptionTests.cs` (5; RED: 2 failed, the no-talent control passed; the two threshold cases failed against the first version).
* Playerbot scenarios, `tests/ArcaneCore.World.Tests/Playerbots/Scenarios/UnitControlScenarioTests.cs` (3; RED: all 3 failed at the control
  step): `control-possess`, `control-charm`, `spirit-of-redemption`. Logs: `D:/ArcaneCore-lanes/_logs/w2-unit-control/`.
