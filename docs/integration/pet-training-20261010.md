# Pet training (lane `pet-training`, wave 19)

Hunter beast training: training-point costs, the family check and the four-active-spell limit. References (read-only, nothing copied):
vmangos (primary, `D:\refs\vmangos`), mangos-classic, mangoszero. Server rules only; no wire change.

## Delivered scope

| Piece | Where | Reference |
|---|---|---|
| `SkillLineAbility.dbc` field 14 (reqtrainpoints) read into `SkillLineAbilityRecord.ReqTrainPoints`; `HasTrainingPoints` is true only for the 15-field image, a 14-field image still loads; `TrainingPoints(spellId)` (first row), `FirstInChain(spellId)` (bounded walk over `PreviousRank`) | `Kernel/Npc/NpcServiceTables.cs`, `Data/Npc/NpcServiceDbcReaders.cs` | vmangos `DBCfmt.h:68`, `DBCStructure.h:555` |
| `CreatureFamily.dbc` skillLine[0] (field 5) by family id | `Data/Content/Pets/CreatureFamilyDbcReader.cs` (`ReadSkillLines`, `LoadSkillLines`) | vmangos `DBCStructure.h:250` |
| `PetTraining`: `Cost` (GetTPForSpell), `HasPoints` (HasTPForSpell), `CanLearn` (CanLearnPetSpell, family line or SKILL_PET_TALENTS 270 for a hunter pet), `CanTakeMoreActiveSpells` (ACTIVE_SPELLS_MAX 4 distinct non-passive chains), `CreateSpellsCost` (usedtrainpoints) | `Game/Pets/PetTraining.cs` | vmangos `Pet.cpp:884-1021`, `:2087-2095`, `Pet.h:132` |
| LEARN_PET_SPELL cast check in vmangos order: NoPet, NotKnown, TooManySkills, Lowlevel (the teach spell's level above the pet's), TrainingPoints | `Game/Pets/SummonService.Tame.cs` (`CheckLearnPetSpell`) | vmangos `Spell.cpp:5837-5862` |
| `EffectLearnPetSpell`: family gate, `SetTrainingPoints(tp - cost)`, learn, save, SMSG_PET_SPELLS; a higher rank replaces the known lower rank (bar slot and active state kept), a lower rank is ignored | `SummonService.Tame.cs`, `CharmInfo.ReplaceRank` | vmangos `SpellEffects.cpp:3329-3353`, `Pet.cpp:1887-1975` |
| Tame: training points become minus the create spells' cost after `PetLoyalty.InitNew` and before the loyalty raise | `SummonService.TameCreature` | vmangos `SpellEffects.cpp:3142, 3152-3154`, `Pet.cpp:2103` |
| Wiring: catalog from DI or `NpcServices:SkillLineAbilityDbcPath`, family lines from `Pets:CreatureFamilyDbcPath`; `Service.Training` is set only when both are present and the catalog `HasTrainingPoints`, otherwise one Information line says the feature is off | `World/Pets/PetsFeature.cs` | `Talents/TalentFeature.BuildRankChain` precedent |

Without the training data the earlier uncosted path stays (a pet learns any teach spell, no cost, no family check); only the NoPet, NotKnown and
Lowlevel checks apply. This matches the `PetFoodMask` fallback.

## Claim ledger (summary)

- VERIFIED against the real build-5875 files: `SkillLineAbility.dbc` is 15 fields (record size 60); field 14 is the cost, field 13 is always 0;
  26 spells listed under several skill lines carry one value in every row (so the first-row lookup is order independent); forward_spellid
  links pet ranks (17253 -> 17255 -> ... -> 17261) with ONE exception found by scanning every costed pet ability by name: Boar Charge rank 6 (27685) has
  no forward link from rank 5 (26201), so `SkillLineAbilityCatalog` adds that single link from the reference spell_chain (mangos-classic
  `sql/archive/0.8/4096_pet.sql:116`, vmangos `sql/old_migrations/20181119025556_world.sql:23` sets spell_chain build_min=5302 for spell 27685); `PetTrainingDbcTests.RealDbc_*` asserts every costed pet ability name resolves to one chain; `CreatureFamily.dbc` is 18 fields with skillLine[0] at field 5 (Wolf 1 -> 208, Felhunter 15 -> 189).
  The real-file tests (`PetTrainingDbcTests.RealDbc_*`) need `ARCANECORE_TEST_DBC_DIR` and are reported as skipped without it.
- SpellCastResult codes (NoPet 0x4C, NotKnown 0x38, TooManySkills 0x89, Lowlevel 0x2B, TrainingPoints 0x79) and UNIT_TRAINING_POINTS 0x95 already existed.
- CONFLICT, resolved to vmangos: vmangos gates EffectLearnPetSpell on the family skill line, mangos-classic does not. The client only offers
  abilities the family can take, so the gate matters for crafted casts only.
- UNVERIFIED, unchanged here: the displayed encoding `GetDispTP` (tp < 0 -> -tp, else -(tp + 1), landed in #73) is emulator-only knowledge.
  Cheapest check: a 5875 capture of UNIT_TRAINING_POINTS for a pet with known free or over-spent points.

## Out of scope (separate lanes)

- Pet untraining: CMSG_PET_UNLEARN 752, SMSG_PET_UNLEARN_CONFIRM 753, GOSSIP_OPTION_UNLEARNPETSKILLS, reset-cost escalation and its persistence (would need characters schema 56).
- Hunters learning abilities from pets (teach spells, `Pet::CheckLearning`, persistence of the teach map) and the owner-side learn of passive teach spells in InitPetCreateSpells.
- Training points granted on pet level-up (`Pet::GivePetLevel`); ArcaneCore has no pet XP.
- SPELL_EFFECT_LEARN_SPELL (36) aimed at the pet (`Spell.cpp:5807-5835`).
- Schema: unchanged (`character_pet.TrainingPoints` already exists), so characters schema 56 is not taken.
