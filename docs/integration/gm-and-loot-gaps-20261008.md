# GM commands and chest-open gaps (2026-10-08)

This lane is based on `ddebfd52`. It adds no schema version or third-party source. All reference behavior below was reimplemented from the read-only vmangos checkout; no GPL source was copied.

## GM commands

| Commands | Behavior and source | Data and limits |
|---|---|---|
| `.tele add`, `.tele del` | Add uses the caller's position and rejects a name matched by the current teleport lookup; delete requires an exact case-insensitive name. The write commits before the world-thread list changes. vmangos `ChatHandler::HandleTeleAddCommand` / `HandleTeleDelCommand`, `Commands/TeleportCommands.cpp:48-106`; `ObjectMgr::AddGameTele` / `DeleteGameTele`, `ObjectMgr.cpp:10555-10604`. | Uses the existing `game_tele` table through `IGameTeleStore` and a serializable transaction. No world schema 43. A later `arcane-content-importer --replace` rewrites that table from its dump, including GM edits. |
| `.modify faction`, `.npc set faction` | Display or change the selected unit's faction and optional unit/NPC/dynamic flags; NPC command changes its current life. vmangos `HandleModifyFactionCommand`, `Commands/UnitCommands.cpp:2140-2207`; `HandleNpcSetFactionIdCommand`, `Commands/CreatureCommands.cpp:530-556`. | The ID must exist in the loaded `FactionTemplateCatalog`. Player targets pass `CanActOn`. No creature spawn or template write. |
| `.reset spells` | Remove current spells, then learn race/class defaults and the learn-spell effects of rewarded quest spells, respecting the first-rank and specialization checks. vmangos `HandleResetSpellsCommand`, `Commands/CharacterCommands.cpp:3873-3890`; `Player::ResetSpells` / `LearnDefaultSpells` / `LearnQuestRewardedSpells`, `Objects/Player.cpp:19263-19369`. | Online player only; refuses while a quest reward is settling. Rewarded quest spells require the loaded quest journal, spell and skill-rank content. Non-learning effects of a reward spell are not replayed. |
| `.reset stats` | Reapply level base stats, derived stats, faction, and health/mana/energy. vmangos `HandleResetStatsCommand`, `Commands/CharacterCommands.cpp:3858-3870`; `Player::InitStatsForLevel(true)`, `Objects/Player.cpp:3254-3411`. | Online player only; needs level-stat rows. Base health/mana plus worn-item and stamina/intellect bonuses rebuild maxima when no active pool aura is present. With a pool aura, maxima are preserved because this code does not remove and reapply that aura. Full vmangos flag, form, skill, talent and pet reset is still a limit. |
| `.bg status`, `.bg start`, `.bg stop` | List matches and every queued player of each queue type, all brackets (vmangos walks `m_queuedPlayers` despite its "your bracket" header, `MiscCommands.cpp:1777-1797`); set the current match start delay to zero; arm the premature-finish countdown with 100 ms left. The stop ends the match only where the premature finish would (`BattleGround::Update`, `BattleGround.cpp:317-357`): not Alterac Valley, in progress, premature finish enabled, and a team below its minimum; the team still at its minimum wins. Otherwise the countdown is cancelled on the next update and the GM is told the match goes on. vmangos `HandleBGStatusCommand` / `HandleBGStartCommand` / `HandleBGStopCommand`, `Commands/MiscCommands.cpp:1715-1840`; `BattleGround::StopBattleGround`, `Battlegrounds/BattleGround.cpp:1857-1861`. | Start/stop require the invoker to be in a match. Status uses loaded battleground templates. Deliberate deviation: a `.bg start` before the first start event fired keeps the delay at zero (vmangos then resets it to two minutes, `BattleGround.cpp:367-379`, so its command does nothing that early); locked in by `BattlegroundGmControlTests.ForceStart_BeforeTheFirstStartEvent_KeepsTheDelayAtZero_ADeliberateDeviation`. |
| `.learn all`, `all_gm`, `all_crafts`, `all_default`, `all_lang`, `all_myclass`, `all_myspells`, `all_mytalents`, `all_mytaxis`, `all_recipes`, `all_trainer`, `all_items`; `.unlearn all_gm`, `all_crafts`, `all_recipes` | vmangos `learnCommandTable` / `unlearnCommandTable`, `Chat/Chat.cpp:492-516`; handlers in `Commands/CharacterCommands.cpp:2572-3175`. Numeric `.learn` stays at developer level 5; the level-1 language child remains reachable. Mass mutation refuses a quest settlement. | Spells, skills, talents, trainer, item, creature and taxi content is required for the corresponding variants. `all` and `all_myspells` skip a spell whose chain's first rank is a talent (`GetTalentSpellCost(GetFirstSpellInChain(id))`, ranks from the SkillLineAbility forward links) and a spell `SpellMgr::IsSpellValid` would reject (`SpellMgr.cpp:2298-2370`: a created item or reagent missing from item_template, recursively through learn effects; skipped when no item content is loaded). `all_mytaxis` uses the nearest known flightmaster spawn on the caller's map, else a spawn elsewhere, then the nearest node of the taxi network (`ObjectMgr::GetNearestTaxiNode`, `ObjectMgr.cpp:7352-7389`: a node with at least one outgoing path that no `SPELL_EFFECT_SEND_TAXI` spell flies, `DBCStores.cpp:366-405`; the lowest id wins a tie); pool spawning is not modelled. `all_trainer` uses loaded `npc_trainer` offers and the trainer GREEN state, but the source's `npc_trainer_template` association is not in ArcaneCore content. No missing content rows are fabricated. |

The existing `CanActOn` rule applies to every variant that mutates a selected player. The commands use the vmangos retail levels in `Chat.cpp:492-516, 593, 679, 917-918, 1004-1005, 1066-1068`; a stored ArcaneCore Administrator maps to retail level 6 by default. The generated [command reference](../reference/gm-commands.md) lists every new path.

Variant-to-function trace (`vmangos/src/game/Commands/CharacterCommands.cpp`):

| Variant | vmangos handler and line |
|---|---|
| `.learn all` | `ChatHandler::HandleLearnAllCommand`, 2572 |
| `.learn all_gm` / `.unlearn all_gm` | `HandleLearnAllGMCommand`, 2670 / `HandleUnLearnAllGMCommand`, 2685 |
| `.learn all_crafts` / `.unlearn all_crafts` | `HandleLearnAllCraftsCommand`, 3046 / `HandleUnLearnAllCraftsCommand`, 3062 |
| `.learn all_default` | `HandleLearnAllDefaultCommand`, 2983 |
| `.learn all_lang` | `HandleLearnAllLangCommand`, 2966; build-5875 `lang_description`, `ObjectMgr.cpp:84-101` |
| `.learn all_myclass` | `HandleLearnAllMyClassCommand`, 2696 |
| `.learn all_myspells` | `HandleLearnAllMySpellsCommand`, 2703 |
| `.learn all_mytalents` | `HandleLearnAllMyTalentsCommand`, 2749 |
| `.learn all_mytaxis` | `HandleLearnAllMyTaxisCommand`, 2942; nearest-spawn choice `ObjectMgr::FindCreatureData`, `ObjectMgr.cpp:11348-11387` |
| `.learn all_recipes` / `.unlearn all_recipes` | `HandleLearnAllRecipesCommand`, 3136 / `HandleUnLearnAllRecipesCommand`, 3161; recipe helpers, 2998-3044 |
| `.learn all_trainer` | `HandleLearnAllTrainerCommand` / `HandleLearnTrainerHelper`, 2794-2888 |
| `.learn all_items` | `HandleLearnAllItemsCommand`, 2890-2940 |

## Chest open and gathering

`LootService.OpenDurableGameObject` now sends `SMSG_LOOT_RELEASE_RESPONSE` if a dungeon chest's first generation is refused by the settlement coordinator (including capacity). It leaves the chest ready and sends no loot window. The same refusal packet is sent for a blocked durable key or an existing bag that the player cannot open. vmangos `Player::SendLootRelease` / `SendLoot`, `Objects/Player.cpp:7597-7633, 7653-7663`, and `WorldSession::DoLootRelease`, `Handlers/LootHandler.cpp:435-487`, provide the refused-window behavior.

A durable generation that commits after its opener died or moved out of range, or finds the spawn gone or another live bag, now also answers the opener with `SMSG_LOOT_RELEASE_RESPONSE`; the committed loot stays on the chest for the next opener.

The gathering spell passes its skill-up callback to the chest open. An ordinary chest runs it only after `SMSG_LOOT_RESPONSE`; a durable chest runs it only after the generation commits and the window actually opens. A refused or unresolved generation never raises the skill. vmangos `Spell::EffectOpenLock`, `Spells/SpellEffects.cpp:2100-2210`, calls `Player::UpdateGatherSkill` at 2201/2207 after its `SendLoot` call; ArcaneCore's asynchronous durable open requires the later completion check. `Player::UpdateGatherSkill` is in `Objects/Player.cpp:5247-5281`. The callback receives the chest that opened, so a durable chest whose grid reloaded during the commit records the skill-up on the object that now tracks the spawn. vmangos rolls for any object target, so a door or button opened by the spell still rolls once it activated (the Codex draft had dropped that roll; restored at intake), and a chest whose script takes the open over (`OnUnlockedUse` / `OnUse` returning true) runs the callback at once, since it never reaches a loot window.

## Verification

Changed production files: `Kernel/WorldData/MapData.cs`; `Data/Content/Maps/{MapDataModule,EfGameTeleStore}.cs`;
`Game/Battlegrounds/{Battleground,BattlegroundManager,BattlegroundQueue}.cs`,
`Game/GameObjects/GameObjectMapSystem.cs`, `Game/Loot/LootService.cs`,
`Game/Npc/QuestNpcServices.Trainer.cs`, `Game/Progression/PlayerProgression.cs`;
`World/Battlegrounds/BattlegroundCommands.cs`, `World/Commands/CommandTable.cs`,
`World/Gm/Character/{FactionCommands,ResetSpellCommands}.cs`, `World/Gm/Npc/GmNpcSetCommands.cs`,
`World/Skills/GatheringSpells.cs`, `World/Spells/{SpellCommands,SpellVariantCommands}.cs`,
`World/Teleport/TeleportCommands.cs` (all under `src/ArcaneCore.*`).

Changed tests: `Data.Tests/ContentImport/Locations/GameTeleStoreTests.cs`;
`Game.Tests/Battlegrounds/BattlegroundGmControlTests.cs`,
`Game.Tests/GameObjects/{DurableChestTests,FakeLootCoordinator}.cs`;
`World.Tests/Battlegrounds/BattlegroundCommandTests.cs`,
`World.Tests/Gm/Character/{FactionCommandTests,ResetSpellCommandTests,ResetStatsCommandTests}.cs`,
`World.Tests/Gm/Teleport/GmTeleEditTests.cs`, `World.Tests/GridTerrain/MapTestData.cs`,
`World.Tests/Spells/SpellVariantCommandTests.cs` (all under `tests/ArcaneCore.*`).

Changed docs: `docs/areas/{group-loot-xp,skills}.md`,
`docs/integration/{gm-command-parity-20261008,gm-and-loot-gaps-20261008}.md`,
and generated `docs/reference/gm-commands.md`.

Synthetic tests cover command levels and effects, SQLite teleport persistence, refused initial chest generation, an existing bag's not-allowed reply, and delayed successful versus refused callbacks.

| Check | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | 0 warnings, 0 errors |
| Focused Data / Game / World tests | 1 / 25 / 34 passed at the focused gates; the variant/docs gate passed 96/96, and the final teleport/docs gate 79/79 |
| Full `ArcaneCore.Game.Tests` | 7,532 passed, 13 skipped, 0 failed |
| Full `ArcaneCore.World.Tests` | 3,191 passed, 29 skipped, 0 failed |
| Full `ArcaneCore.Data.Tests` | The unrestricted run was stopped after a sustained silent stall. Excluding `Resilience`: 1,211 passed, 15 skipped, 0 failed. `GameTeleStoreTests` passed separately. |

No real 5875 client or imported-world acceptance was run here.

## Intake (native rerun)

Fixes at intake: the gathering spell rolls the skill for a door or button again (only a chest waits for its loot window), the chest-open callback passes the opened object, and a brace was re-indented in `TeleportCommands`. New tests: `InstanceChestDurabilityTests.GatheringOpen_OfADungeonNode_RaisesTheSkillOnlyWhenItsGenerationCommitted` (a mining cast on a dungeon node whose generation fails raises nothing; the committed retry raises once), `GatheringWorldTests.AnOpenLockOfADoor_RaisesTheSkillOnceItActivates`, and a `HideUnavailable` case of `SpellVariantCommandTests.LowerRankLanguageChild_IsReachableThroughTheHigherRankLearnRoot`. Each was shown to fail with its fix removed, as was `DurableChestTests.RefusedInitialGeneration_ReleasesTheClient_AndLeavesTheChestReady` without the refusal packet.

| Check (native, 2026-10-08) | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | 0 warnings, 0 errors |
| Full `ArcaneCore.Game.Tests` | 7,532 passed, 13 skipped, 0 failed |
| Full `ArcaneCore.World.Tests` | 3,194 passed, 29 skipped, 0 failed |
| Full `ArcaneCore.Data.Tests` (Resilience included) | 1,340 passed, 15 skipped, 0 failed |
| Full `ArcaneCore.Kernel.Tests` | 32 passed, 0 failed |

## Review rework (2026-10-08)

| Reviewer item | Resolution | Test (shown failing with the fix removed) |
|---|---|---|
| 1 (material) `.bg stop` ended every match with no winner, AV included, paying loser marks and AV tower/captain honor | `Battleground.ForceStop` now only arms the premature-finish countdown (100 ms), exactly as `StopBattleGround`; the existing premature-finish update decides, so the winner is the team at its minimum and every other case is cancelled. No end-of-match rewards are paid for a stop vmangos would ignore. The command reports whether the match will end. | `BattlegroundGmControlTests`: `ForceStop_WithATeamBelowItsMinimum_EndsAfterOneHundredMilliseconds_AndTheTeamAtItsMinimumWins`, `..._WithBothTeamsAtTheirMinimum_IsCancelled_AndALaterLeaverGetsTheFullCountdown`, `..._BeforeTheGatesOpen_IsCancelled`, `..._InAlteracValley_NeverEndsTheMatch_AndPaysNothing` (they replace the old `Winner=None` test; the old behaviour fails the first and fourth by construction) |
| 2 scripted chest open never rolled a gathering skill-up | `GameObjectMapSystem.OpenLock` runs the chest-open callback when a script takes the open over | `GatheringWorldTests.AChestWhoseScriptTakesTheOpenOver_StillRaisesTheSkillOnce` |
| 3 `.learn all` taught higher ranks of talent chains; no `IsSpellValid` | `FirstRankIsTalent` checks the chain's first rank (also in `all_myspells`, which had the same gap); `IsSpellValid` ported | `SpellVariantCommandTests.LearnAll_AndMySpells_SkipHigherRanksOfTalentChains_AndBrokenSpells` (RED separately for the talent chain and the broken spell) |
| 4 `all_mytaxis` ignored the taxi network | Nearest node restricted to the vmangos network set, lowest id on a tie | `SpellVariantCommandTests.TaxiTrainerAndItemVariants_UseLoadedContent` (a closer path-less node is skipped) |
| 5 `.bg status` counted one bracket; `.bg start` deviation undocumented | Counts every queued player of the queue type by group team; the start deviation is documented above and in `Battleground.ForceStart` | `BattlegroundManagerTests.QueuedTeamCounts_CountEveryBracketOfTheQueue_LikeVmangosBgStatus`; `ForceStart_BeforeTheFirstStartEvent_KeepsTheDelayAtZero_ADeliberateDeviation` |
| 6 committed generation without a reply (pre-existing) | `LootService.FinishGeneration` refuses the opener on every path that shows no window | `DurableChestTests.CommittedGeneration_ForAnOpenerWhoDiedMeanwhile_ReleasesTheClient_AndKeepsTheLoot` |
| 7 verified OK | No change | - |

Known pre-existing gap, not changed here: `NpcStore.TaxiNodesMask` holds every node rather than vmangos' network set, and `QuestNpcServices.NearestTaxiNode` (flightmaster hello discovery) has no network filter either. `all_mytaxis` computes the network itself.

| Check (native, rework) | Result |
|---|---|
| `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` | 0 warnings, 0 errors (DLL timestamps checked) |
| Full `ArcaneCore.Game.Tests` | 7,537 passed, 13 skipped, 0 failed |
| Full `ArcaneCore.World.Tests` | 3,196 passed, 29 skipped, 0 failed (includes the regenerated `gm-commands.md` golden) |
