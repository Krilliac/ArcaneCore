# Profession coverage audit (2026-10-08)

Worktree `codex/w2-professions`, based on main `92966fdd`. The local reference sources are
`D:/refs/vmangos`, `D:/refs/mangos-classic` and the read-only z2815 ClassicDB dump. This is a code and
synthetic/loopback-test audit; it is not a build-5875 client acceptance run.

| Path | Current coverage and reference | Remaining limit |
|---|---|---|
| Trainer and recipe learning | Trainer purchases cast their teaching spell (`Player::GetTrainerSpellState`, Player.cpp:4233-4297; `HandleTrainerBuySpellOpcode`, NPCHandler.cpp:266-335). `CMSG_USE_ITEM` gates recipe items by required skill, rank and spell, then LEARN_SPELL teaches the craft (`Player::CanUseItem`, Player.cpp:10075; `Spell::EffectLearnSpell`, SpellEffects.cpp:2435-2454). `RecipeLearningTests` covers both and now pins one z2815 recipe for each named specialization. | A real-client recipe/trainer round trip is pending. |
| Crafting | Reagents and tools are checked before costs and effects (`Spell::CheckItems`, Spell.cpp:7249-7306; `Spell::TakeReagents`, Spell.cpp:5082-5128). A triggered child whose parent has no first reagent still checks its own tool (`Spell::IgnoreItemRequirements`, Spell.cpp:7069-7083). CREATE_ITEM stores output and calls `PlayerSkills.UpdateCraft` after a successful store (`Spell::DoCreateItem`, SpellEffects.cpp:1885-1990). `SkillRules.CraftChance` and `PlayerSkills.UpdatePro` follow the color thresholds and per-mille roll (`Player::UpdateCraftSkill`, `UpdateSkillPro`, Player.cpp:5203-5243, 5298-5339). Item random properties are rolled by `StoreNewItem` if the optional DBC and enchantment template data is present (`Item::GenerateItemRandomPropertyId`, Item.cpp:792-834; `DoCreateItem`, SpellEffects.cpp:1950). | Without optional item random-property content, outputs have no property. Triggered casts with a foreign trade item need an end-to-end trade test. |
| Focus objects | `SpellFocusCastCheck` searches spawned focus objects by `RequiresSpellFocus` id and radius; anvils, forges and fires use the same check (`Spell::CheckItems`, Spell.cpp:7233-7243; `GameObjectFocusCheck`, GridNotifiers.h:586-606). Equipment and reagent checks run in reference order. | Terrain and real-client placement have not been checked here. |
| Herbalism and mining | `GatheringSpells` checks Lock.dbc skill cases and required ranks, rolls the orange failure at cast landing, opens loot and calls `UpdateGather`. A per-player/node set prevents repeat skill-ups until respawn (`Spell::CanOpenLock`, Spell.cpp:7869-7923; `Spell::EffectOpenLock`, SpellEffects.cpp:2191-2207; `Player::UpdateGatherSkill`, Player.cpp:5247-5281). Mineral veins can stay ready after a loot release up to their data's maximum use count (`WorldSession::DoLootRelease`, LootHandler.cpp:435-487). | The runtime needs the build-5875 Lock.dbc and imported node/loot content for real nodes. |
| Skinning | Level gates, prior corpse loot, tapper head start, orange failure, elite double chance and skin loot are present (`Spell::CheckCast` SKINNING, Spell.cpp:5940-5969; `Spell::EffectSkinning`, SpellEffects.cpp:5371-5390). | Real creature data and multiplayer tap/loot acceptance remain to be checked. |
| Fishing | `FishingService.Catch` checks the area's skill, pool override, loot and `FailGain`, then calls `PlayerSkills.UpdateFishing` on success or configured failure (`GameObject::Use`, GameObject.cpp:1635-1731; `Player::UpdateFishingSkill`, Player.cpp:5283-5296). The bobber and pole cast checks have loopback tests. | Lure temporary enchant and pole-skill bonuses are not applied to the catch skill; actual water, visual and pool behavior needs client acceptance. |
| First aid and cooking | Cooking uses the regular recipe/item craft path. First aid also has the bandage on-hit Recently Bandaged script (`FirstAidScript::OnAfterHit`, mangos-classic spell_item.cpp:577-594) and channel/immunity tests. | Real-client bandage and cooking interaction remains pending. |
| Specializations | ClassicDB assigns `npc_prof_blacksmith`/`npc_prof_leather` to the relevant trainers and has the eight vanilla branch teaching spells. `ProfessionSpecializationGossip` now ports ScriptDev2 `GossipHello_npc_prof_blacksmith`, `SendActionMenu_npc_prof_blacksmith`, `IsEligibleSpecializeLW`, `GossipHello_npc_prof_leather` and `SendActionMenu_npc_prof_leather`. Offers and selection both check skills, level, reputation or rewarded quest and mutually exclusive known spells. Specialization recipe items still use the generic `RequiredSpell` check, including Gnomish (20219) and Goblin (20222) engineering. | The source ScriptDev2 file explicitly lacks engineering unlearn/relearn. Its blacksmith unlearn action cases are absent; the leather unlearn spells 36328, 36433 and 36434 are absent from z2815 `spell_template`. No unlearn option is offered. The no-cost extra confirmation menu on weapon subdiscipline learning is also not ported. Engineering quest and book specialization acquisition still need a dedicated acceptance path. |

The earlier `docs/areas/skills.md` and `docs/areas/crafting.md` limits had drifted: crafting,
random-property rolls, key items and vein repeat opens already existed. They now point to the
current owners. No schema change or world content write is part of this lane.

## Data and acceptance still needed

`D:/refs/client-dbc-5875-effective` currently has ItemRandomProperties.dbc and
SpellItemEnchantment.dbc, but no Lock.dbc, Faction.dbc or FactionTemplate.dbc. The first is needed
for real mining and herbalism lock cases; the faction files are needed for the reputation and
interaction services that authorize the ScriptDev2 blacksmith trainers. The six leatherworking
specialization quests and their NPC/quest rows must be present in the imported world content.
The z2815 dump has the profession NPC ScriptName assignments and teaching spells, but its
leatherworking unlearn spell IDs 36328/36433/36434 are absent. These missing inputs are not
replaced by synthetic rows in production. Fishing, crafting and specialization user flows have
not been observed with a real build-5875 client in this lane.

## Files and verification

- Game code: `src/ArcaneCore.Game/Crafting/{ProfessionSpecializationGossip,ReagentRules}.cs`,
  `src/ArcaneCore.Game/Npc/{NpcGossipScript,QuestNpcServices.Gossip}.cs`,
  `src/ArcaneCore.Game/Spells/{SpellCombatSeams,SpellSystem.Reagents,SpellSystem.Seams,SpellSystem}.cs`.
- World wiring: `src/ArcaneCore.World/Crafting/CraftingFeature.cs`.
- Tests: `tests/ArcaneCore.Game.Tests/Crafting/{CreateItemEffectTests,ProfessionSpecializationGossipTests,ReagentTests,RecipeLearningTests}.cs`,
  `tests/ArcaneCore.World.Tests/Crafting/CraftingFeatureTests.cs`. The specialization test first failed to compile
  without the new handler; the triggered-child tool test first failed `CastOk` versus `ItemGone`.
- Docs: `docs/README.md`, `docs/areas/{crafting,skills}.md`, this audit.

Free RAM was checked before each build. The requested CIM query was denied by the local environment, so
`Microsoft.VisualBasic.Devices.ComputerInfo.AvailablePhysicalMemory` supplied the physical-memory check;
each build started above 3 GiB. `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`
completed with **0 warnings and 0 errors**. Filtered Game crafting tests: **113 passed, 0 failed**;
the final specialization-gossip filter: **5 passed, 0 failed**.
Final `dotnet test tests/ArcaneCore.Game.Tests -c Release --no-build --no-restore`:
**7,093 passed, 0 failed, 11 skipped**. Final World project with the same test flags:
**2,522 passed, 0 failed, 6 skipped**. The skips are environment-gated and were not counted as acceptance.
No server was started; no commit or push was made.

## Intake (2026-10-08)

The coordinator intake aligned the specialization gossip with ScriptDev2's wire values: senders
`GOSSIP_SENDER_MAIN` (1), `GOSSIP_SENDER_LEARN` (50) and `GOSSIP_SENDER_CHECK` (52); actions
`GOSSIP_ACTION_TRADE` (1), `GOSSIP_ACTION_TRAIN` (2) and `GOSSIP_ACTION_INFO_DEF` (1000) + n; the vendor,
trainer and chat icons; and the reference's line texts. It also added a test that the wrapped gossip
script (the Alterac Valley collectors) still serves every other creature. The gossip wrapper relies on
features attaching in type-name order (`BattlegroundFeature` posts its `??=` install before
`CraftingFeature` posts the wrapper).

Intake verification: Release build 0 warnings, 0 errors. Full Game tests **7,094 passed, 11 skipped,
0 failed**; full World tests **2,522 passed, 6 skipped, 0 failed**. Breaking the triggered-child tool
rule, the selection recheck, the wrapper fall-through and the feature install each failed the matching
test (4 Game tests and 1 World test).
