# Melee combat

Branch `feat/combat`. Code lives in `src/ArcaneCore.Game/Combat/` (rules, state and packets)
and `src/ArcaneCore.World/Combat/` (opcode handlers). Tests are
`tests/ArcaneCore.Game.Tests/Combat*.cs` and `tests/ArcaneCore.World.Tests/Combat*.cs`.

References used: vmangos (`src/game/Objects/Unit.cpp`, `SpellCaster.cpp`, `Player.cpp`,
`Creature.cpp`, `Handlers/CombatHandler.cpp`, `Handlers/MiscHandler.cpp`, `QueryHandler.cpp`,
`Server/Packets/Combat.cpp`, `MovementPacketSender.cpp`), gtker wow_messages (`.wowm` files
for 1.12), cmangos-classic as a cross-check.

## Implemented

### Auto-attack
- **CMSG_ATTACKSWING (u64 guid).** vmangos `WorldSession::HandleAttackSwingOpcode`. An
  unknown target gets SMSG_ATTACKSTOP with no victim. A friendly, unselectable, spawning or
  dead target, or one that fails `CanAttack`, gets SMSG_ATTACKSTOP naming the enemy.
  Otherwise `Unit::Attack`.
- **CMSG_ATTACKSTOP (empty).** `HandleAttackStopOpcode` calls `AttackStop`.
- **SMSG_ATTACKSTART:** u64 attacker, u64 victim. vmangos `Unit::SendMeleeAttackStart`;
  gtker `combat/smsg_attackstart.wowm`. Sent to self and observers.
- **SMSG_ATTACKSTOP:** packed attacker, packed victim, u32. vmangos
  `Unit::SendMeleeAttackStop`; gtker `combat/smsg_attackstop.wowm`.
- **SMSG_ATTACKERSTATEUPDATE.** Layout: u32 hitInfo, packed attacker, packed target, u32
  total damage, u8 sub-damage count (1), then per sub-damage u32 school, f32 damage, u32
  damage, u32 absorb, i32 resist. Then u32 victim state, u32 unknown (0), u32 spell id (0),
  u32 blocked. Sources: vmangos `Unit::SendAttackStateUpdate` (Combat.cpp) and gtker
  `combat/smsg_attackerstateupdate.wowm`. The packet is sent before the damage is applied
  (vmangos `AttackerStateUpdate`).
- **Swing errors.** SMSG_ATTACKSWING_NOTINRANGE, _BADFACING, _DEADTARGET and _CANT_ATTACK
  are sent only when the error changes. A failed check delays both hands by 100 ms. Source:
  vmangos `Unit::UpdateMeleeAttackingState`.
- **Melee reach.** max(5, attacker combat reach + victim combat reach + 4/3), plus 2.66 yd
  of leeway when both units move and neither walks. Source: vmangos `Unit::CanReachWithMeleeAutoAttack`
  and `GetLeewayBonusRange`. Facing is a 2π/3 arc (`HasInArc`).
- **Swing timers.** Per hand, from UNIT_FIELD_BASEATTACKTIME. The off hand is pushed to
  200 ms if both hands are ready together.
- **Parry haste** (vmangos `Unit::DealMeleeDamage`). When a victim parries, its next swing
  moves to 20% of its attack time if it was between 20% and 60%. Above 60%, it moves 40% of
  the attack time sooner.
- **CMSG_SETSHEATHED (u32).** Sets UNIT_FIELD_BYTES_2 byte 0 (vmangos `HandleSetSheathedOpcode`).

### Hit table
`MeleeHitTable.Roll` is a port of vmangos `SpellCaster::RollMeleeOutcomeAgainst`, together
with `GetMeleeMissChance`, `Unit::GetUnitCriticalChance` and the dodge/parry/block chances.
- **Order.** One roll on 0–9999 against stacked ranges: miss, dodge, parry, glancing, block,
  crit, crushing, normal.
- **Evade and immune.** Evading targets give EVADE. Immune is not handled yet.
- **Miss.** 5%, minus 0.04 per skill point against players. Against creatures it is 0.1 per
  point, or 0.2 per point when the skill gap is over 10. Creatures below level 10 scale it
  by level/10.
  - Dual wield adds 19%.
  - +hit applies, except the first 1% when the gap is over 10.
  - Capped at 60%.
- **From behind.** Nobody parries or blocks from behind, and players cannot dodge from
  behind. Creatures parry and block unless flagged otherwise.
- **Glancing.** Player attackers against non-player victims: 10% + 2% per point of victim
  defense over the attacker's skill, capped at 40%. The skill used is capped at level×5. The
  damage multiplier is frand(low, high) from vmangos `Unit::CalculateMeleeDamage`; the bounds
  are not swapped.
- **Crushing.** Creatures whose level×5 exceeds the victim's capped defense by 15 or more:
  2% per point − 15%.
- **Sitting victims.** Swings never miss a victim that is not standing. A sitting player is
  always crit by creatures, and by players with any crit chance.
- **Armor** (vmangos `SpellCaster::CalcArmorReducedDamage`). r = 0.1·armor / (8.5·level + 40),
  and the reduction is r / (1 + r), capped at 75%. At least 1 damage remains.
- **Crits** deal 200% (vmangos `CalculateMeleeDamage`, MELEE_HIT_CRIT).
- **Crushing blows** deal 150%.
- **Block** removes the shield block value: level/2 + strength/20 for creatures, and
  strength/20 − 1 for players.

### Damage, rage, combat flags, threat
- `DealDamage`, following vmangos `Unit::DealDamage`:
  - A sitting victim stands up.
  - Both units enter combat.
  - Rage is granted, then the kill or the health loss.
  - An idle player victim attacks back.
  - Threat is added; a creature that is missed gets 0 threat.
- **Rage:** vmangos `Player::RewardRage`.
  - Conversion = 0.0091107836·L² + 3.225598133·L + 4.2652911.
  - Dealing damage gives damage/conv·7.5; taking it gives damage/conv·2.5; both ×10.
  - Taken rage is ×1.3 under Berserker Rage (18499, effect 0), and the result is ×`Combat:RateRageIncome`
    (`PowerRules.RageFromDamage`, `Player.cpp:2243-2267`).
- **Combat state:** vmangos `Unit::SetInCombatState`. UNIT_FLAG_IN_COMBAT lasts 5.5 s
  after PvP, batched to the 1-second check (`BatchifyTimer`). Units stay in combat while
  attacking, being attacked or on a threat list. SMSG_CANCEL_COMBAT is sent on stop (vmangos
  `Unit::CombatStop`).
- **ThreatList** (vmangos `ThreatManager`):
  - Stable, sorted by threat.
  - The current victim keeps aggro until another unit reaches 110% in melee range or 130%
    out of it.
  - Kills remove the victim from every threat list.

### Death, release, corpse, reclaim
- **Kill:** vmangos `Unit::Kill`.
  - Sends SMSG_PARTYKILLLOG (u64 killer, u64 victim) to the killer's set.
  - Calls `CombatHooks.OnKill` and `ICombatCreature.OnJustDied`.
  - Sets JUST_DIED, health 0 and power 0, roots the victim and stops combat.
  - Creature victims go straight to CORPSE.
- **KillPlayer:** vmangos `Player::KillPlayer`.
  - CORPSE state; dynamic flags cleared.
  - PLAYER_FIELD_BYTES release-timer flag (0x08) on non-instanced maps.
  - 6-minute auto release (`CORPSE_REPOP_TIME`), skipped in instances.
  - The recent-death window grows by 5 minutes per death, up to 3.
- **CMSG_REPOP_REQUEST:** vmangos `HandleRepopRequestOpcode` → `BuildPlayerRepop`.
  - Ghost flag; SMSG_MOVE_WATER_WALK.
  - Health 1; unrooted.
  - A corpse object at the body (vmangos `Player::CreateCorpse`: owner, display, bytes,
    items, flags, PvP type) is visible to observers.
  - SMSG_CORPSE_RECLAIM_DELAY (u32 ms), then the graveyard hook.
- **Reclaim delay:** `GetCorpseReclaimDelay` gives 30/60/120 s by recent deaths.
- **CMSG_RECLAIM_CORPSE (u64 corpse guid):** vmangos `HandleReclaimCorpseOpcode`.
  - The delay must have passed.
  - The ghost must be within 39 yd (`CORPSE_RECLAIM_RADIUS`) of the corpse.
  - Resurrects at 50% health, removes the ghost and the corpse, and sends SMSG_MOVE_LAND_WALK.
- **MSG_CORPSE_QUERY:** vmangos `HandleCorpseQueryOpcode`.
  - Not found: u8 0.
  - Found: u8 1, i32 map, f32 x/y/z, i32 corpse map.
- **Logout:** the player's corpse is removed and its attackers and threat are detached
  (`WorldRuntime.PlayerLoggingOut`).

### PvP flag
CMSG_TOGGLE_PVP: an optional u8 state (gtker `pvp/cmsg_toggle_pvp.wowm`, vmangos
`HandleTogglePvP`).
- PLAYER_FLAGS_PVP_DESIRED; turning it on flags UNIT_FLAG_PVP at once.
- Turning it off lets a 5-minute timer run out, paused during PvP combat (`Player::UpdatePvPFlagTimer`).
- Attacking a flagged player flags the attacker (`TogglePlayerPvPFlagOnAttackVictim`).

### Aura states and reactive abilities (warrior-mechanics S07)
`AuraStateService` and `ReactiveService` (`Combat/Reactive/`, installed by `ReactiveFeature`), after vmangos
`Unit::ModifyAuraState` (`Unit.cpp:4682-4745`) and `ProcSkillsAndReactives` (`:8834-8915`):

- **States.** `UNIT_FIELD_AURASTATE` bit (state - 1). Setting a state casts the passives a player knows that need it as caster
  state; clearing it removes the auras that need it. The 20% health state follows `health < 0.2 x max` every tick for players
  and units in combat (`Unit.cpp:318-319`) and goes when the unit dies.
- **Checks.** A spell's caster aura state is required (`CASTER_AURASTATE`, also for triggered casts, `Spell.cpp:5392`). The
  only target state vmangos checks is the 20% one (Execute): no target is `BAD_IMPLICIT_TARGETS`, a healthy target `BAD_TARGETS`,
  after the power check (`:5733-5742`).
- **Windows.** A dodge, parry or block opens a 4 s window (`REACTIVE_TIMER_START`): the victim gets Defense (Revenge, Riposte;
  a rogue's dodge excepted), a parrying hunter gets HunterParry and a combo point (Counterattack), and a warrior whose attack was
  dodged gets the Overpower marker, a combo point on the victim. When the window ends the state or the marker goes; a death clears
  every window. White swings report through `MapCombat.MeleeSwingResolved` (before the packet and the damage, like vmangos'
  proc call); melee and ranged class spells report their per-target outcome through the spell observer.
- **Limits.** The custom states 9-11 (health 15, 10, 5 percent) are not 1.12 data and are not implemented; the Berserking crit
  state exists only for clients up to 1.8.4 and is omitted; the creature health states and speed changes are the creature area's;
  melee spell Block outcomes appear once the spell hit table produces them; Overpower's "cannot be dodged, parried or blocked"
  attribute is honoured by the hit table slice, not here.

### Combo points (warrior-mechanics S09)
`ComboPointService` (`Combat/Combo/`, installed by `ComboFeature`) follows vmangos `Player::AddComboPoints`,
`ClearComboPoints` and `SetComboPoints` (`Player.cpp:19032-19093`):

- **State.** Up to 5 points on one target; points on the current target add up, a new target restarts the count; negative
  counts floor at 0. `PLAYER_FIELD_COMBO_TARGET` and `PLAYER_FIELD_BYTES` byte 1 are written only while the target can be found
  (vmangos `SetComboPoints`). 1.12 has no combo point packet. Every change ends `SPELL_AURA_RETAIN_COMBO_POINTS` auras.
- **Warriors.** A warrior holds at most the Overpower marker (see the aura state section); the client shows none and the
  server uses it only to allow the cast (`Spell.cpp:7035-7038`).
- **Finishing moves** (`NeedsComboPoints`): the check (spell cast check, phase Power) needs points on the explicit target:
  `NO_COMBO_POINTS` for a rogue, `BAD_TARGETS` for a warrior; triggered casts and spells without an explicit unit target skip it.
- **Scaling.** An effect adds `EffectPointsPerComboPoint x points` on the combo target (`SpellCaster.cpp:1190-1192`); a duration
  stretches toward its maximum by points/5 (`SpellEntry.cpp:731-735`) for any spell whose base and maximum durations differ, as in
  vmangos. The effect bonus is added to the truncated value (a fractional base plus a fractional bonus can differ by 1).
- **Spending.** A finishing move that completes clears the points (`Spell.cpp:4374-4395`), except a harmful one that missed, was
  dodged or parried on a target other than the caster. `SPELL_EFFECT_ADD_COMBO_POINTS` (80) adds its value.
- **Gone.** The target dying, the owner dying and the owner logging out clear the points (`Unit.cpp:9410-9422`, `Player.cpp:1519`);
  a unit that leaves the world without dying does not (limit).

### Melee spells and the swing (warrior-mechanics S08b)
The swing reaches the spell system through `IMeleeSpellHooks` (`CombatEnvironment.MeleeSpells`, installed by the
world daemon's `MeleeSpellFeature` as `SpellSystemMeleeHooks`; without it nothing is cast from a swing):

- **No swing while casting.** `MapCombat.AttackerStateUpdate` returns without a swing while the unit has a generic
  cast or a channel in progress (vmangos `Unit::AttackerStateUpdate`, `Unit.cpp:2239-2240`). The attack timer still
  restarts, so the swing is lost, not delayed. `Combat:MeleeCastingBlocksSwing` (default true, retail) turns it off.
- **Queued next-swing spell.** The main-hand swing casts the spell queued in `UnitSpellState.MeleeCast` at the victim
  instead of the white hit (`Unit.cpp:2249-2257`): power is taken, effects applied, SMSG_SPELL_GO sent. A cast that fails
  (no rage left) drops the spell and the white hit is still lost, as vmangos returns when the slot is empty. The
  off-hand never fires it; an out-of-range swing waits.
- **Attack stop.** `AttackStop` (also a target switch and `CombatStop`) interrupts the queued spell
  (`Unit.cpp:4604`).
- **One environment per world.** `CombatEnvironment` carries the options and the links; `PowerFeature`,
  `MeleeSpellFeature` and `StanceFeature` share it (`CombatEnvironments.GetOrCreate` binds `Combat` once).

### Power economy (warrior-mechanics S03)
The rates live in `CombatOptions` (config section `Combat`: `RateRageIncome`, `RateRageLoss`, `RateEnergy`,
`RateMana`; defaults 1 = retail, `mangosd.conf.dist.in:2793-2799`). `Rate.Mana` and `Rate.Rage.Loss` fall back to 1 when
negative (`World::setConfigPos`, `World.cpp:2959-2967`); the other two are not validated, like vmangos. The world
daemon's `PowerFeature` binds them and registers a `CombatEnvironment` (options plus an aura source backed by the spell
system) for the world; worlds without it use `CombatEnvironment.Default`.

- **Refund.** `PowerRefundObserver` (a spell cast observer) returns `round(cost x 0.82)` of the power of an
  `EX_DISCOUNT_POWER_ON_MISS` ability: energy on miss, dodge, parry or immune, rage on dodge or parry only
  (`Spell.cpp:1267-1285`; the comment there says 80%, the code 0.82).
- **Limits.** Health regeneration is unchanged (no `Rate.Health`, no regen-in-combat auras); the mana regen
  ignores spirit-regen auras; rage from abilities is none (only white swings, as in both cores).

### Regeneration
- **Players**, every 2 s (vmangos `Player::RegenerateAll` / `Regenerate`):
  - Out of combat: health by spirit per class (`GetRegenHPPerSpirit`, ×1.5 sitting) and rage
    −2 (×`Combat:RateRageLoss`, not while a SPELL_AURA_INTERRUPT_REGEN aura such as Bloodrage is on).
  - Always: energy +20 (×`Combat:RateEnergy`).
  - Mana: `GetRegenMPPerSpirit`·2·`Combat:RateMana`, or 0 within 5 s of spending mana.
  - Rage and energy ticks are also scaled by MOD_POWER_REGEN_PERCENT auras for that power
    (`Player.cpp:2323-2328`).
  - See "Power economy" above.
- **Creatures**, every 5 s out of combat: a third of max health/mana (vmangos
  `Creature::RegenerateHealth` / `RegenerateMana`).

## Discrepancies and known gaps
- **SMSG_ATTACKSTOP final u32.** vmangos `Unit::SendMeleeAttackStop` sends the attacker's
  `health == 0`, the session path sends the enemy's `IsDead`, cmangos sends the attacker's
  `IsDead`, and gtker calls it unknown. We follow vmangos `Unit` (attacker health == 0).
- **School field.** gtker labels it a school mask; vmangos sends the school index. We send
  the index (0 = physical).
- **HitInfo bits.** gtker lacks the ROLLED_*, BLOCK, BLOOD_SPURT and PVP bits that vmangos
  `HitInfo` sends; we send them as vmangos does.
- **Corpse lifetime.** Corpses are removed (out-of-range block) instead of turning to bones
  (DESTROY_OBJECT/bones corpse).
- **Graveyard.** No teleport yet; the ghost stays at the corpse.
- **Ghost form.** The ghost flag and water walk are set directly instead of through aura
  8326/20584, so there is no ghost speed.
- **Unit state.** UNIT_FLAG_STUNNED stands in for the root/unit state.
- **Player stats.** Weapon damage, attack speed, attack power, crit/dodge/parry/block
  percentages and the agility part of armor are now written by the player stat system
  ([stats.md](stats.md)), so a player with a weapon rolls the weapon's range plus attack
  power instead of the 0–5 fallback. `MapCombat.Stats` (`ICombatStatSource`) answers off-hand
  weapon, parry, block and the shield block value for players; a map without the stats
  feature keeps the `CombatHooks` defaults. Still open: one sub-damage per swing, one armor
  value for every school, aura modifiers. Skills default to level×5.
- **Durability.** No durability loss and no SMSG_DURABILITY_DAMAGE_DEATH.
- **Emotes.** No EMOTE_ONESHOT_WOUNDCRITICAL.
- **Create blocks.** No UPDATEFLAG_MELEE_ATTACKING.
- **Visibility.** Ghosts and the living are not separated.
- **Persistence.** Death state and corpses are not persisted across logout.
- **Corpse query in dungeons.** The ghost entrance needs Map.dbc; we return the corpse
  position.
- **Creature combat.** Creature combat exit/evade and in-combat mana belong to the AI and
  creatures areas.
- **Logout while dead.** CancelLogout could unroot a dead player.
- **Water walk ack.** CMSG_MOVE_WATER_WALK_ACK is unhandled (accepted silently by the
  client).
- **Attack checks.** The handler refuses `!CanAttack` up front, where vmangos lets
  `Unit::Attack` decide.
- **Reclaim timing.** The reclaim delay uses map-local elapsed time instead of wall-clock
  seconds.

## Real-client acceptance (1.12.1)
1. Log in a Human (A) and an Orc (B) on map 0 and `/pvp` on both (CMSG_TOGGLE_PVP).
   Expected: the PvP flag shows on both portraits.
2. A targets B, stands in front within 5 yd and right-clicks.
   Expected: both clients show the attack animation, combat text appears every 2 s (the
   unarmed attack time), and the health bar drops.
3. Turn A away.
   Expected: "You are facing the wrong way!" once. Walk away: "Target is too far away." once.
4. Keep attacking until B dies.
   Expected: B gets the release dialog with a countdown. A's combat log shows a kill.
5. B clicks Release Spirit.
   Expected: ghost (the client applies the screen effect from the ghost flag), a corpse at
   the body visible to A, and a 30 s reclaim timer on B.
6. After 30 s, B walks to the corpse and clicks Resurrect.
   Expected: B is alive at half health and the corpse disappears for A.
7. Repeat steps 4–6 within 5 minutes.
   Expected: the reclaim timer is 60 s.
8. Sit B, hit it.
   Expected: B stands up. Check rage on both warriors after hits, and that rage decays out of
   combat.

## What's left
- Graveyard teleport.
- Ghost aura and speed.
- Durability loss.
- Bones corpses and corpse persistence.
- Ghost visibility.
- Creature-side AI combat (evade, leash, chase).
- Ranged auto-shot.
- Spell damage paths beyond `DealDamage`.
- Stat-driven damage and percentages.
- Immune results.
- Duel and FFA PvP rules.
