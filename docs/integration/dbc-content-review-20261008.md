# Build-5875 DBC and ClassicDB cross-reference review (2026-10-08)

Source set: the 154 files in `D:/ArcaneCore-data/client-dbc-5875`, read by `arcane-db dbc --json` at `ddebfd52` with an empty local SQLite world database. The baseline client-only report checked 174 foreign-key columns: 151 clean, 23 dangling (152 distinct ids summed over the columns, 341 rows), none skipped. WoWDBDefs build 1.12.1.5875 `definitions/*.dbd` at `e3df370` supplies the field order and `COLUMNS` annotations; the generator validates primary vmangos `src/game/Database/DBCfmt.h` field counts and record sizes. The cited vmangos `src/game/Database/DBCStructure.h` fields confirm the independent field meaning where vmangos loads them. The source below names each DBC row ID and value; each target ID set was read from that target file's DBD `ID` field, not inferred from a neighboring table.

**Verdict:** one WoWDBDefs foreign-key annotation is wrong for this build; no ArcaneCore column-offset error was found. The other 22 groups contain actual references to absent target IDs in this client extraction. They are now listed by exact ID in `DbcKnownClientGaps.cs`. A new absent ID in one of those columns remains an unexpected `Dangling` result. These findings do not license synthetic replacement DBC records.

| Source column → target ID | Missing IDs / rows | Example source row → missing value | Verdict and evidence |
| --- | ---: | --- | --- |
| AreaTable.ContinentID → Map | 2 / 2 | 67 → 17 | Known client gap; Map has 44 IDs, neither 17 nor 150. WoWDBDefs `AreaTable.dbd` / `Map.dbd`; vmangos `DBCfmt.h` supplies the formats used by `DBCStores.cpp::LoadDBC`. |
| AreaTable.IntroSound → ZoneIntroMusicTable | 4 / 4 | 24 → 67 | Known client gap; target has 43 IDs and lacks 65, 67, 68, 71. `AreaTable.dbd`, `ZoneIntroMusicTable.dbd`. |
| AreaTrigger.ContinentID → Map | 2 / 3 | 60 → 24 | Known client gap; Map lacks 24 and 28. `AreaTrigger.dbd`; vmangos `DBCfmt.h` supplies the primary format. |
| CreatureModelData.SoundID → CreatureSoundData | 1 / 1 | 1011 → 908 | Known client gap; target has 406 IDs and lacks 908. `CreatureModelData.dbd`; vmangos `DBCStructure.h::CreatureModelDataEntry` field 13. |
| CreatureSoundData.SoundExertionCriticalID → SoundEntries | 9 / 9 | 2 → 457 | Known client gap; SoundEntries has 4,623 IDs, lacks the nine values in the known-ID list. `CreatureSoundData.dbd`. |
| CreatureSoundData.SoundInjuryID → SoundEntries | 2 / 2 | 114 → 1025 | Known client gap; also 2521. `CreatureSoundData.dbd`. |
| CreatureSoundData.SoundInjuryCriticalID → SoundEntries | 2 / 2 | 127 → 1303 | Known client gap; also 387. `CreatureSoundData.dbd`. |
| CreatureSoundData.SoundStunID → SoundEntries | 3 / 3 | 26 → 690 | Known client gap; also 191 and 199. `CreatureSoundData.dbd`. |
| CreatureSoundData.SoundStandID → SoundEntries | 9 / 9 | 2 → 461 | Known client gap; nine absent target IDs. `CreatureSoundData.dbd`. |
| CreatureSoundData.SoundFidget → SoundEntries | 17 / 18 | 16 → 1017 | Known client gap; four-element array, including the absent 5514–5529 range. `CreatureSoundData.dbd`. |
| FootstepTerrainLookup.CreatureFootstepID → SpellVisualEffectName | 5 / 50 | 21 → 8 | **Wrong foreign key.** `FootstepTerrainLookup.dbd` annotates `SpellVisualEffectName::ID`, but `CreatureSoundData.dbd` annotates `SoundFootstepID` with `FootstepTerrainLookup::CreatureFootstepID`. The 17 observed footstep-group values include 7, 8, 13, 16 and 137, which are absent from SpellVisualEffectName; those values also occur in CreatureSoundData.SoundFootstepID. This is a category key, so the generator drops the false ID foreign key. |
| SkillLineAbility.SupercededBySpell → Spell | 1 / 1 | 936 → 2997 | Known client gap; Spell has 22,357 IDs and lacks 2997. `SkillLineAbility.dbd`; vmangos `DBCStructure.h::SkillLineAbilityEntry::forward_spellid`. |
| SkillRaceClassInfo.SkillID → SkillLine | 35 / 53 | 59 → 96 | Known client gap; SkillLine has 123 IDs and lacks these 35, including 242. `SkillRaceClassInfo.dbd`; vmangos `DBCStructure.h::SkillRaceClassInfoEntry::skillId`. |
| Spell.EffectTriggerSpell → Spell | 16 / 16 | 1233 → 875 | Known client gap; 16 triggered spell IDs absent from Spell's ID set. `Spell.dbd`; vmangos `DBCfmt.h` supplies the primary Spell field layout. |
| SpellItemEnchantment.EffectArg → Spell | 3 / 3 | 9 → 2820 | Known client gap; rows 9–11 have effect type 1 (combat spell) and arguments 2820–2822. vmangos `DBCStructure.h::SpellItemEnchantmentEntry` and `Objects/Player.cpp::ApplyEnchantment` type dispatch treat type 1 as a spell ID; `SpellItemEnchantment.dbd` agrees. |
| SpellVisual.ImpactKit → SpellVisualKit | 1 / 1 | 129 → 210 | Known client gap; target has 1,772 IDs and lacks 210. `SpellVisual.dbd`; vmangos `DBCStructure.h::SpellVisualEntry::impactKit`. |
| SpellVisualKit.SoundID → SoundEntries | 16 / 27 | 13 → 23 | Known client gap; 16 absent sound IDs. `SpellVisualKit.dbd`. |
| TaxiNodes.ContinentID → Map | 1 / 1 | 81 → 131074 | Known client-only exceptional value (`Filming` node), not a Map ID; Map has 44 IDs. `TaxiNodes.dbd`; vmangos `DBCStructure.h::TaxiNodesEntry::map_id`. It must not be imported as a playable map. |
| TaxiPathNode.PathID → TaxiPath | 2 / 99 | 5221 → 248 | Known client gap; paths 248 and 403 absent from the 287 TaxiPath IDs. `TaxiPathNode.dbd`; vmangos `DBCStructure.h::TaxiPathNodeEntry::path`. |
| UISoundLookups.SoundID → SoundEntries | 11 / 11 | 33 → 820 | Known client gap; 11 absent sound IDs. `UISoundLookups.dbd`. |
| WMOAreaTable.ZoneMusic → ZoneMusic | 2 / 3 | 22583 → 198 | Known client gap; target has 99 IDs and lacks 198 and 260. `WMOAreaTable.dbd`; vmangos `DBCStructure.h::WMOAreaTableEntry` field 7. |
| WMOAreaTable.IntroSound → ZoneIntroMusicTable | 7 / 22 | 13777 → 75 | Known client gap; seven absent intro IDs. `WMOAreaTable.dbd`; vmangos `DBCStructure.h::WMOAreaTableEntry` field 8. |
| ZoneMusic.Sounds → SoundEntries | 1 / 1 | 234 → 7340 | Known client gap; two-element sound array; target lacks 7340. `ZoneMusic.dbd`. |

The corrected generator was run twice against the same 154 files; both outputs had SHA-256 `1F800CB58A0F9D3D6BB68FCB3CD90C066EECFE0E8238F19FF1BAD4851F63F060`. The resulting scan checks 173 foreign-key columns: 151 clean, 22 known client-data gaps (147 IDs, 291 rows), zero unexpected dangling, zero skipped. `arcane-db dbc` still treats client-internal gaps as diagnostic; its exit code remains driven by malformed files and world-DB drift.

## World-DB IDs in the wave-6 report

These values occur **in the source** `D:/refs/classic-db/Full_DB/ClassicDB_1_12_1_z2815.sql.gz` INSERT rows. They were not generated by ArcaneCore's import. The import column mapping is in `ContentTableSpecs.cs` (`gameobject_template` fields), `ItemQuestDumpImporter.cs` / `ItemTemplateRow.cs` (`displayid`, `RequiredSkill`), and `NpcDumpImporter.cs` (`reqskill`). vmangos `src/game/ObjectMgr.cpp::LoadGameobjects`, `LoadItemPrototypes`, and `LoadTrainers` read the corresponding source columns; `Objects/ItemPrototype.h::RequiredSkill`, `Objects/Player.cpp::GetTrainerSpellState`, and `Handlers/NPCHandler.cpp::HandleTrainerListOpcode` use them as display, faction or skill IDs. This rules out an ArcaneCore column swap in the rows reviewed here.

| World column / absent client ID | ClassicDB rows and example | Verdict |
| --- | --- | --- |
| gameobject_template.Faction 1660, 1732, 1733, 1735, 1751 | 1, 12, 1, 13, 1 rows respectively (28 total); `Flame of Stormwind` entry 181332 has 1735, `Mailbox` 182939 has 1733 | ClassicDB content uses faction templates absent from this 314-row FactionTemplate.dbc. Keep source values and document the missing client references. |
| gameobject_template.DisplayId 11686 | Entry 176510, `Onyxia Whelp Spawner` | ClassicDB content uses a display absent from the 1,638-row GameObjectDisplayInfo.dbc. No inferred display substitute. |
| item_template.display_id 37829 | Entry 25818, `Monster - Shield, Legion` | ClassicDB content uses a display absent from the 29,604-row ItemDisplayInfo.dbc. |
| item_template.required_skill 242 | Entry 7869, `Lucius's Lockbox` | ClassicDB source really has RequiredSkill=242; SkillLine.dbc lacks 242. vmangos `Objects/ItemPrototype.h::RequiredSkill` says this is a SkillLine ID. |
| npc_trainer.reqskill 10 | 14 rows; trainer 3033/spell 17376 | ClassicDB source uses 10; SkillLine.dbc lacks 10. vmangos `ObjectMgr::LoadTrainers` reads it unchanged. |
| npc_trainer.reqskill 17862 | 16 rows; trainer 461/spell 17938 | ClassicDB source uses 17862 in the skill column. It is **present in Spell.dbc**, absent from SkillLine.dbc. This is a source-data semantic error, not an import conversion; a correct replacement skill cannot be inferred from the dump. |

The earlier wave-6 world report's `area_template.MapId` 17/150, `areatrigger_template.MapId` 24/28 and `taxi_nodes.map_id` 131074 are imported client references, matching the client-only rows above. No world schema version or content rows were changed. A refreshed world DB should be rerun through `arcane-db dbc`; this review used the wave-6 cross-reference for world counts and the ClassicDB dump for direct row evidence.

## Verification

- The two new targeted tests failed before the code change: the footstep field still had `SpellVisualEffectName` as its foreign table, and the known AreaTable map 17 still returned `Dangling`.
- `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: succeeded, 0 warnings, 0 errors.
- `ARCANECORE_TEST_DBC_DIR=D:/ArcaneCore-data/client-dbc-5875 dotnet test tests/ArcaneCore.Data.Tests -c Release --no-build --no-restore --filter FullyQualifiedName~DbcCrossReferenceTests`: 10 passed, 0 skipped, 0 failed.
- Full `ArcaneCore.Data.Tests` with the same client directory: 1,348 passed, 8 skipped, 0 failed. The first full run had one documentation skip-attribution failure; after updating `docs/integration/wave4-integration.md`, the rerun passed.
- Rebuilt `arcane-db dbc --json` against the 154 files and an empty local SQLite world database: exit 0, 173 client columns checked, 151 `Ok`, 22 `KnownClientGap`, 0 `Dangling`, 0 `Skipped`. The 84 world checks skipped because that scratch database had no content tables; this is not a fresh world-DB qualification.
- Intake rerun (native, after the review fixes: invariant-culture summary line restored, known-gap lookup no longer depends on array order, the summary line's known-gap clause asserted): the generator rerun into a scratch file is byte-identical to the committed `ClientDbcLayouts.Dbd.g.cs`; Release solution build 0 warnings, 0 errors; `DbcCrossReferenceTests` 10/10 with the client directory. Breaking both features (footstep key restored, `DbcKnownClientGaps.Contains` forced false) failed 3 of the 10 (the override fact, the synthetic known-gap fact and the real-set fact); restored, full `ArcaneCore.Data.Tests` passed 1,348 with 8 skipped with the client directory and 1,340 with 16 skipped without any variable; `ArcaneCore.World.Tests` `ClientData` 9 passed, 5 skipped; `ArcaneCore.Game.Tests` `Hygiene` 21 passed.
