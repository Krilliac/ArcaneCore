# Playerbot progression: talents, gear, rewards, upkeep, quests

Managed playerbots (docs/areas/playbots.md) now grow the way a player does: they spend talent points, wear better gear and
weapons, pick useful quest rewards, repair, keep bag space, buy ammunition, and take every quest the server settles under the
default configuration. Everything goes through the client's own packets and the real handlers (`WorldSession.TryManagedAction`):
the server decides legality, the bot only chooses. Code: `src/ArcaneCore.World/Playerbots/Progression/`,
`PlayerbotEquipment.cs`, `PlayerbotTownGoals.cs`, `PlayerbotQuestGoals.cs`, `PlayerbotWorldDestinations.cs`.

## Out-of-combat upkeep (one request per think)

`PlayerbotEquipment.Update(player)` is the brain's existing out-of-combat hook (after resting, before quests and town goals);
it does nothing in combat, while casting, dead, or without action budget, and sends at most one request:

1. **Bags** into an empty bag slot (CMSG_AUTOEQUIP_ITEM; general containers only, largest first).
2. **Gear**: the carried item that gains most over what it would replace (`PlayerbotItemScore`), worn through
   CMSG_AUTOEQUIP_ITEM, or CMSG_AUTOEQUIP_ITEM_SLOT when the target is not the slot the server would pick itself (the second
   ring or trinket, the worse of two). Armor, weapons, shields, off-hands, rings, trinkets, cloaks, relics, items with stats and
   random suffixes all count; shirts, tabards, ammunition and quivers do not. A two-handed weapon is compared with both hands;
   nothing goes into the off hand beside a two-hander. Broken items (durability 0) score nothing. vmangos
   CombatBotBaseAI::EquipOrUseNewItem (CombatBotBaseAI.cpp:2958) equips whatever it is given by destroying the old item;
   mangoszero's EquipAction sends the client packet: the bot does the latter, choosing by score. A request the server refuses is
   not a fault: the item is left alone for a minute.
3. **Ammunition**: with a bow, gun or crossbow worn and no usable ammunition selected, the best fitting ammunition in the bags
   (item level, then damage) through CMSG_SET_AMMO (vmangos AddHunterAmmo, :2908, creates and sets it).
4. **Talents**: one rank (below).

## Talents

`PlayerbotTalents` spends while the talent service reports free points: one rank per think through CMSG_LEARN_TALENT, out of
combat and within the action budget. It does nothing when the talent feature is inert (no Talent.dbc configured).

**Builds** (`PlayerbotTalentBuilds`): one to three premade builds per class, each 51 points in learning order. vmangos
LearnPremadeSpecForClass (CombatBotBaseAI.cpp:2407) applies `player_premade_spec` rows from the world database, which the
references do not carry, so these are authored. A pick names a talent by *(page, row, column)*, the page being the tab's index
among the class's tabs in `TalentCatalog.TabsForClassMask` order, so a build resolves against any catalog, synthetic or real.
With the build-5875 TalentTab.dbc the mage's Fire and Arcane tabs both have order 0: page 0 is Fire (tab 41), page 1 Arcane.
Each specialised build reaches the talent vmangos AutoAssignRole (CombatBotBaseAI.cpp:58) checks for its role:

| Class | Builds (bot id modulo the count picks one) | Role talent |
|---|---|---|
| Warrior | arms 31/20/0, protection 11/0/40, fury 17/34/0 | Shield Slam (protection) |
| Paladin | holy 31/20/0, protection 16/35/0, retribution 20/0/31 | Holy Shield, Sanctity Aura |
| Hunter | marksmanship 20/31/0, beast mastery 37/14/0 | |
| Rogue | combat swords 19/32/0, assassination 31/20/0 | |
| Priest | holy 19/32/0, shadow 20/0/31 | Shadowform (shadow) |
| Shaman | elemental 31/0/20, enhancement 20/31/0, restoration 0/14/37 | Elemental Mastery, Stormstrike |
| Mage | frost 0/20/31 (Fire/Arcane/Frost pages), fire 31/20/0 | |
| Warlock | destruction 20/0/31, demonology 20/31/0 | |
| Druid | balance 31/0/20, feral 0/33/18, restoration 14/0/37 | Moonkin Form, Leader of the Pack |

Healer builds reach none of the role talents (AutoAssignRole then gives the healer role). Every build is checked twice by
tests: against a synthetic catalog of its own positions (tier gates in order, all 51 points from the build) and, when
`ARCANECORE_TEST_DBC_DIR` names a directory with the developer's Talent.dbc and TalentTab.dbc, against the real catalog (all 51
points from the build, the role talent reached; otherwise that test reports Skipped). Nature's Grasp needs the class spell
Entangling Roots (Talent.dbc DependsOnSpell), trained at level 8.

**Planner** (`PlayerbotTalentPlanner.Next`, pure): the next missing rank of the first unfinished pick, pre-checked with the
server's own `TalentRules.EvaluateLearn`. When a pick has no talent in the catalog, the server would refuse it, or the handler
already refused it, the build is set aside for vmangos LearnRandomTalents (:2459): one tab, talents in tier order. vmangos picks
the tab and the order inside a tier at random; here the tab is the one with the most points (else the build's first page, else
one from the bot id) and a tier is walked by column, so a bot always continues where it stopped. When that tab has nothing
legal left the other tabs follow, so points never stay unspent while a legal talent exists.

**Refusals**: CMSG_LEARN_TALENT has no reply. After each request the bot reads its spellbook; a rank that did not appear is
remembered and not asked again until the level or the free points change.

**Bot id**: the character's guid (low part). The same bot always gets the same build and therefore the same item weights.

## Item scores

`PlayerbotItemScore.Score`: stats times the build's `PlayerbotStatWeights`, plus armor, block value, weapon damage per second
(melee, ranged or wand weight by sub-class) and a small item-level tie-break; the stats an item carries in its enchantment slots
(random suffixes, enchants) count when the enchanting feature has a SpellItemEnchantment catalog. A build's weapon style
(two-hand, one-hand and shield, dual wield, caster) discounts hands it does not use. The weights are the build's own
(unifying them with the brain's combat role is later work).

## Quest rewards

At the reward choice the bot takes the usable item that gains its build most over what it wears
(`PlayerbotItemScore.ChooseQuestReward`), else the choice that sells for most (sell price times count), instead of choice 0.
Indexes are into the dense choice list the turn-in validates.

## Town upkeep (`PlayerbotTownGoals`, visible NPCs)

- **Repair**: any worn item broken or under 25% of its durability, at an NPC with the repair flag: CMSG_REPAIR_ITEM with an
  empty item guid (repair everything; mangoszero RepairAllAction). When nothing improved (no repair prices loaded, no money),
  repairs pause for a minute.
- **Full bags**: with no free slot in the backpack or worn general bags, the cheapest junk is sold first: white or gray items
  that are not protected (quest items and quest starters, food and drink, ammunition and other bag-family items, reagents,
  items with a binding or a skill requirement) and are no equipment upgrade.
  Gray items are still sold at any vendor visit, as before.
- **Hunter ammunition**: a hunter with a bow, gun or crossbow and fewer than 200 fitting rounds buys the best fitting ammunition
  the vendor sells (one CMSG_BUY_ITEM buys the item's BuyCount); the upkeep then selects it.
- Food and drink and trainer spells as before. The bot does not travel to a repairer; it uses one it can see.

## Quests under Quests:RewardMode

Since 229faf27 `Quests:RewardMode` is `AllSupported` by default: every quest whose needs have adapters settles, with no
allowlist. The bot consulted `OrdinaryRewardQuestIds` alone and therefore never accepted a quest on a default server. It now asks
`QuestNpcServices.IsRewardable(questId)`, the same gate the turn-in uses: every supported quest by default, the allowlist only
under `AllowlistOnly`. Quest travel (`PlayerbotWorldDestinations`) considers, under `AllSupported`, every rewardable quest the
character is old enough for and that is not gray for it (vmangos GetGrayLevel), and keeps the 128 nearest giver and ender spawns;
under `AllowlistOnly` the allowlist, as before.

## Scenarios (`Scenarios/ScenarioProgression.cs`)

The bots are set up scripted, then handed back to their brain (`GoAutonomousAsync`); the steps wait for what the brain does.

| Scenario | What | Content |
|---|---|---|
| `progression-talents` | a level-12 warrior spends its three points by itself; ranks and free points survive a relog | a talent catalog (live-safe; in the catalog) |
| `progression-gear` | a warrior wears the better weapon and ring from its bags | item entries in the constructor (internal: not in the catalog) |
| `progression-repair` | a warrior with broken armor repairs at a repair NPC and pays | NPC and item entries in the constructor (internal) |
| `progression-quest` | a fresh warrior takes, completes and turns in a kill quest under the server's Quests config | quest and giver in the constructor (internal) |

Tests: `tests/ArcaneCore.World.Tests/Playerbots/Scenarios/BotProgressionScenarioTests.cs` (the extra content is registered
through `ScenarioTestWorld.StartAsync(configure)`; the quest test installs a flat floor and straight paths and clears the
allowlist), unit tests under `tests/ArcaneCore.World.Tests/Playerbots/Progression/`.
