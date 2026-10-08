# Rates

Every rate knob of the server in one place: the vmangos `Rate.*`, `SkillGain.*`, `SkillChance.*` and `DurabilityLoss*` options of
`mangosd.conf.dist.in` (:1527-1558, :2793-2858; read in `World::LoadConfigSettings`, World.cpp:490-554, 705-716) mapped to their
ArcaneCore keys, plus the few rates ArcaneCore adds. Default 1 (or the vmangos default where it is not 1) is the retail behaviour.
The full description of every key is in the generated [configuration reference](../reference/configuration.md).

**Live** means `.reload config` applies a changed value to the running server. Only the player speed rates are live (and
`.movement set` changes them at runtime); every other rate is read when its feature starts, so a change needs a restart, as the
reference table's `Reload` column says (`-`: not part of the reload set).

## Movement speed (non-retail when not 1)

Not in vmangos. The model is the MaNGOS Zero fork's `Movement.PlayerSpeedRate` and `Movement.{Run,Swim,Walk}SpeedRate`
(feature/movement-enhancements, WorldConfig.cpp, percents 10-1000 multiplied into the player block of `Unit::UpdateSpeed`,
UnitSpeed.cpp:162-184). Here they are multipliers clamped to 0.1-10, run back and swim back have their own rate (the fork's run and
swim rates cover them), and there is a turn rate. Neither vmangos nor the fork has creature speed rates, so there are none.

| Key | Default | Scales | Live |
|---|---|---|---|
| `Locomotion:PlayerSpeedRate` | 1 | every player speed (run, run back, swim, swim back, walk); the per-type rates multiply on top | yes |
| `Locomotion:PlayerRunSpeedRate` | 1 | player run speed | yes |
| `Locomotion:PlayerRunBackSpeedRate` | 1 | player run-back speed | yes |
| `Locomotion:PlayerSwimSpeedRate` | 1 | player swim speed | yes |
| `Locomotion:PlayerSwimBackSpeedRate` | 1 | player swim-back speed | yes |
| `Locomotion:PlayerWalkSpeedRate` | 1 | player walk speed | yes |
| `Locomotion:PlayerTurnRate` | 1 | player turn rate (base 3.141594 rad/s); `PlayerSpeedRate` does not apply | yes |
| `Locomotion:GhostRunSpeedWorld` | 1 | every speed of a player in the CORPSE state outside battlegrounds (`Death.Ghost.RunSpeed.World`) | no |
| `Locomotion:GhostRunSpeedBattleground` | 1 | the same in a battleground (`Death.Ghost.RunSpeed.BG`) | no |

How it works: the rates are copied onto each player (`LocomotionState.ConfiguredSpeedRates`) and `UnitSpeed.SetRate` multiplies the base
speed by them, so the aura recomputation, every `SMSG_FORCE_*_SPEED_CHANGE`, the create block and the server's own idea of the speed all
carry the effective value. A loading character gets them before the world sees it (no force packets at login); `.reload config` and
`.movement set` re-send the speeds of every online player (only the types that change). The turn rate is sent as
`SMSG_FORCE_TURN_RATE_CHANGE` and taken by the server at once (it is not in the pending-change ledger); its ack relays
`MSG_MOVE_SET_TURN_RATE` to the observers.

GM commands (Administrator, written to the GM command audit, a set also logged with old and new value):

* `.movement rates` shows the rates and the effective multipliers.
* `.movement set $field $value` with `$field` one of `speedrate`, `run`, `runback`, `swim`, `swimback`, `walk`, `turn` and `$value`
  0.1-10. It lasts until the next `.reload config` or restart, which take the file's value again.

## Experience, rest, reputation, honor, talents

| vmangos key | ArcaneCore key | Default | Scales | Live |
|---|---|---|---|---|
| Rate.XP.Kill | `Progression:RateXpKill` | 1 | kill XP of a normal creature | no |
| Rate.XP.Kill.Elite | `Progression:RateXpKillElite` | 1 | kill XP of an elite creature | no |
| Rate.XP.Quest | `Quests:RateXpQuest` | 1 | quest XP | no |
| Rate.XP.Explore | `World:Exploration:RateXp` | 1 | exploration XP | no |
| Rate.XP.Personal.Min | `Progression:RateXpPersonalMin` | 1 | lowest rate `.modify xprate` accepts (**new**) | no |
| Rate.XP.Personal.Max | `Progression:RateXpPersonalMax` | 1 | highest rate a player below GameMaster may set (**new**) | no |
| Rate.Rest.InGame | `Rest:RateInGame` | 1 | rested pool growth while online | no |
| Rate.Rest.Offline.InTavernOrCity | `Rest:RateOfflineInTavernOrCity` | 1 | rested pool growth logged out at an inn or city | no |
| Rate.Rest.Offline.InWilderness | `Rest:RateOfflineInWilderness` | 1 | rested pool growth logged out elsewhere | no |
| Rate.Reputation.Gain | `Reputation:RateGain` | 1 | every reputation change (vmangos CalculateReputationGain scales losses too) | no |
| Rate.Reputation.LowLevel.Kill | `Reputation:RateLowLevelKill` | 0.2 | reputation from gray kills | no |
| Rate.Talent | `Talents:PointsRate` | 1 | talent points per level | no |
| Rate.RespecBaseCost | `Talents:RespecBaseCostGold` | 1 | first respec price (gold) | no |
| Rate.RespecMultiplicativeCost | `Talents:RespecMultiplicativeCostGold` | 5 | respec price step (gold) | no |
| Rate.RespecMinMultiplier | `Talents:RespecMinMultiplier` | 2 | lowest respec multiplier | no |
| Rate.RespecMaxMultiplier | `Talents:RespecMaxMultiplier` | 10 | highest respec multiplier | no |
| (Rate.Honor, fork/cmangos only) | `World:Honor:Rate` | 1 | honor earned from kills, battleground bonuses and quests; not dishonor or `.honor add` (**new**, non-retail when not 1) | no |

`.modify xprate #rate` (**new**, vmangos HandleModifyXpRateCommand, CharacterCommands.cpp:62-92): a player sets its own personal XP
rate, a GameMaster the selected player's (above the maximum too). `Player::GiveXP` multiplies every gain by it (Player.cpp:3018-3019).
It is not saved, as in vmangos.

## Power and health regeneration, fall damage

| vmangos key | ArcaneCore key | Default | Scales | Live |
|---|---|---|---|---|
| Rate.Health | `Combat:RateHealth` | 1 | spirit health regeneration | no |
| Rate.Mana | `Combat:RateMana` | 1 | mana regeneration | no |
| Rate.Rage.Income | `Combat:RateRageIncome` | 1 | rage from damage | no |
| Rate.Rage.Loss | `Combat:RateRageLoss` | 1 | out-of-combat rage decay | no |
| Rate.Energy | `Combat:RateEnergy` | 1 | energy regeneration | no |
| Rate.Damage.Fall | `Locomotion:RateDamageFall` | 1 | fall damage | no |

## Loot and money

| vmangos key | ArcaneCore key | Default | Scales | Live |
|---|---|---|---|---|
| Rate.Drop.Item.Poor | `Loot:DropItemPoorRate` | 1 | chance of an ungrouped poor (grey) loot row (**new**) | no |
| Rate.Drop.Item.Normal | `Loot:DropItemNormalRate` | 1 | the same, common (white) (**new**) | no |
| Rate.Drop.Item.Uncommon | `Loot:DropItemUncommonRate` | 1 | the same, uncommon (green) (**new**) | no |
| Rate.Drop.Item.Rare | `Loot:DropItemRareRate` | 1 | the same, rare (blue) (**new**) | no |
| Rate.Drop.Item.Epic | `Loot:DropItemEpicRate` | 1 | the same, epic (purple) (**new**) | no |
| Rate.Drop.Item.Legendary | `Loot:DropItemLegendaryRate` | 1 | the same, legendary (orange) (**new**) | no |
| Rate.Drop.Item.Artifact | `Loot:DropItemArtifactRate` | 1 | the same, artifact (**new**) | no |
| Rate.Drop.Item.Referenced | `Loot:DropItemReferencedRate` | 1 | chance of an ungrouped reference row (**new**) | no |
| Rate.Drop.Money | `Loot:MoneyRate` | 1 | creature, chest, container and pickpocket money | no |
| Rate.Drop.Money | `Quests:RateDropMoney` | 1 | quest reward money (vmangos uses the one key for both) | no |
| Rate.Corpse.Decay.Looted | `Loot:LootedCorpseDecayRate` | 0 | how long a looted-out corpse stays (0: a third of the respawn delay) | no |

The drop rates follow vmangos `LootStoreItem::Roll` (LootMgr.cpp:256-268): a chance of 100 or more always drops, otherwise the row rolls
`chance * rate`; the quality is the item template's (an unknown item uses 1). Grouped rows are never scaled (`LootGroup::Roll`) and a
reference keeps the rate of the table that reached it. Every loot table rolled here allows rates (vmangos turns them off only for mail).

## Creatures

| vmangos key | ArcaneCore key | Default | Scales | Live |
|---|---|---|---|---|
| Rate.Creature.Normal.HP | `Creatures:Rates:NormalHp` | 1 | spawn health of rank 0 (**new**) | no |
| Rate.Creature.Elite.Elite.HP | `Creatures:Rates:EliteHp` | 1 | rank 1, and any unknown rank (**new**) | no |
| Rate.Creature.Elite.RAREELITE.HP | `Creatures:Rates:RareEliteHp` | 1 | rank 2 (**new**) | no |
| Rate.Creature.Elite.WORLDBOSS.HP | `Creatures:Rates:WorldBossHp` | 1 | rank 3 (**new**) | no |
| Rate.Creature.Elite.RARE.HP | `Creatures:Rates:RareHp` | 1 | rank 4 (**new**) | no |
| Rate.Creature.*.Damage | `Creatures:Rates:{Normal,Elite,RareElite,WorldBoss,Rare}Damage` | 1 | melee and ranged weapon damage (**new**) | no |
| Rate.Creature.*.SpellDamage | `Creatures:Rates:{Normal,Elite,RareElite,WorldBoss,Rare}SpellDamage` | 1 | done side of the creature's damage spells (**new**) | no |
| Rate.Creature.Aggro | `Creatures:AggroRate` | 1 | aggro radius | no |

vmangos `Creature::_GetHealthMod` / `_GetDamageMod` / `_GetSpellDamageMod` (Creature.cpp:1856-1911), applied in `SelectLevel`
(:1802-1843: health `max(1, round(rate * health))`, base health too, weapon damage times the rate), `SpellDamageBonusDone`
(SpellCaster.cpp:1588-1590) and `MeleeDamageBonusDone` for a non-weapon spell (:1358-1359). A creature takes the rates at spawn and every
respawn. Pets, guardians and mini pets (pet GUIDs) never use them (vmangos uses 1 for a player's pet, Pet.cpp:1359-1360); a totem's spell
damage uses its owner's bonus, so 1.

## Skills, gathering, durability, auctions, instances

| vmangos key | ArcaneCore key | Default | Scales | Live |
|---|---|---|---|---|
| SkillGain.Crafting | `Skills:GainCrafting` | 1 | points per crafting skill-up | no |
| SkillGain.Defense | `Skills:GainDefense` | 1 | points per defense skill-up | no |
| SkillGain.Gathering | `Skills:GainGathering` | 1 | points per gathering (and fishing) skill-up | no |
| SkillGain.Weapon | `Skills:GainWeapon` | 1 | points per weapon skill-up | no |
| SkillChance.Orange / Yellow / Green / Grey | `Skills:ChanceOrange` / `ChanceYellow` / `ChanceGreen` / `ChanceGrey` | 100 / 75 / 25 / 0 | skill-up chance by colour (percent) | no |
| SkillChance.MiningSteps / SkinningSteps | `Skills:MiningSteps` / `Skills:SkinningSteps` | 75 / 75 | skill-up decay steps (0: none) | no |
| SkillFail.Loot/Gain.Fishing, SkillFail.Possible.FishingPool | `SpecialLoot:Fishing:FailLoot` / `FailGain` / `FailPossibleFishingPool` | false / false / true | failed fishing casts | no |
| Rate.Mining.Amount | `GameObjects:MiningAmountRate` | 1 | uses of a mining node | no |
| Rate.Mining.Next | `GameObjects:MiningNextRate` | 1 | chance of another use | no |
| DurabilityLoss.Enable | `Items:DurabilityLossEnable` | true | durability loss on/off | no |
| DurabilityLossChance.Damage | `Items:DurabilityLossChanceDamage` | 0.5 | percent chance per hit taken | no |
| (DurabilityLossChance.Absorb / Parry / Block, mangos only) | `Items:DurabilityLossChanceAbsorb` / `Parry` / `Block` | 0 | mangos wear chances, off as in vmangos | no |
| Rate.Auction.Time | `Economy:AuctionRateTime` | 1 | auction durations | no |
| Rate.Auction.Deposit | `Economy:AuctionRateDeposit` | 1 | auction deposit | no |
| Rate.Auction.Cut | `Economy:AuctionRateCut` | 1 | auction house cut | no |
| Rate.InstanceResetTime | `World:Instances:RateResetTime` | 1 | raid reset period in days, at least one day; 0 stays 0 (**new**) | no |

`Rate.InstanceResetTime` follows `ObjectMgr::LoadMapTemplate` (ObjectMgr.cpp:6809-6811): `max(1, (uint)(reset_delay * rate))`; the
schedules already stored keep their next reset, the new period applies from then on.

## Not implemented

| vmangos key | Why |
|---|---|
| Rate.Focus | Applied to hunter pet focus regeneration (Pet.cpp:774); this base regenerates no pet focus, so a key would be a stub. |
| Rate.Loyalty | Applied to hunter pet loyalty (Pet.cpp:800); there is no loyalty system here. |
| Rate.WarEffortResourceComplete | Belongs to the Ahn'Qiraj war effort, which is not implemented. |
| Rate.Skill.Discovery, Rate.XP.PetKill, Rate.Mining.{Lower,Rare,Darkiron,Autopooling}, Rate.Reputation.LowLevel.Quest | Keys of the MaNGOS Zero fork / mangos line, not vmangos (classic 1.12 has no skill discovery; the others need systems this base does not have or replace vmangos rules). |
