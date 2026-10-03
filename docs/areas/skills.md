# Area: Skills and professions

Lane `claude/vw-skills-professions`. The cross-area contract (schema version, shared-file edits,
configuration, collisions) is in [docs/integration/skills.md](../integration/skills.md).

Everything here follows the standing directive: as close to vanilla 1.12.1 as possible, taken from the
references, with every deviation behind a configuration option or listed below. References, in order of
authority: **vmangos** (`D:\refs\vmangos`, only the 1.12.1 branch of its `#if SUPPORTED_CLIENT_BUILD` blocks),
**mangos-classic**, **gtker wow_messages**, **classic-db** (read only, ids and numbers as test fixtures, nothing
copied). Each piece of code cites file:line.

## Delivered

**Content** (`Kernel/Skills`, `Data/Skills`; slice `skills-content`)
- `SkillIds`, `SkillCategories`, `SkillRaceClassFlags`, `SkillRangeType` (vmangos `SharedDefines.h:941-1112`,
  `DBCEnums.h:150-181`, `ObjectMgr.h:391-397`).
- DBC readers with strict field counts (vmangos `DBCfmt.h:67-70`): SkillLine 22, SkillRaceClassInfo 8, SkillTiers 33,
  SkillLineAbility 15. **SkillLineAbility previously required 14**; vmangos and mangos-classic both declare 15
  (`niiiixxiiiiixxi`, the last field is `reqtrainpoints`), so the reader now accepts 15 and still reads 14. Which width
  the developer's own file has is unmeasured until the env-gated probe runs (see Acceptance).
- `SkillCatalog`: first-fit `SkillRaceClassInfo` lookup in file order (`DBCStores.cpp:589-602`, a mask of 0 is "any"),
  `GetSkillRangeType` (`ObjectMgr.cpp:10450-10464`), the profession predicates (`SpellMgr.h:256-275`),
  `SpellLearnSkillTable` (`SpellMgr.cpp:1850-1887`, derived from the SKILL effects) and `SpellRankChains`
  (`SpellMgr.cpp:1440-1650`) with the two reference patches (2366 forwards to 2368, 20154 never starts a chain).

**Player skills** (`Game/Skills`; slice `skills-player-model`)
- `PlayerSkills` owns the 127 slots at `PLAYER_SKILL_INFO_1_1 + 3 * slot` (`Player.cpp:90-98`): word 0 id and step,
  word 1 value and maximum, word 2 temporary (low) and permanent (high) bonus.
- `Set` (add, update, remove: `Player.cpp:5504-5648`), `Update`, `UpdatePro` (returns true on a missed roll while the
  skill could still rise: `:5291-5339`), `UpdateCraft`, `UpdateGather`, `UpdateFishing`, `UpdateCombatSkills`,
  `UpdateSkillsForLevel`, `UpdateSkillsToMax`, spell-trained skills and skill-trained spells (`:5710-5896`), forgotten
  weapon skills (client builds above 1.10.2), free primary profession points (`PLAYER_CHARACTER_POINTS2`), weapon and
  armor proficiency masks, the known-language mask, dual wield/parry/block flags, `Load` (`_LoadSkills`,
  `:20540-20660`) and a replace-snapshot state.
- `SkillRules`: every formula pure, with golden tests (gather chances, the combat curve, `GetConfigMaxSkillValue`).
- `PlayerItemRequirements` replaces the "every skill is 300" stand-in of `DefaultItemRequirements`, so equip, lock and
  quest gates read real skills.
- `SMSG_SET_PROFICIENCY` (0x0127: u8 class, u32 mask).

**Persistence** (`Data/Skills`, `World/Skills/SkillSaveCoordinator`; slice `skills-persistence`)
- Characters schema `CharacterSkillsDataModule.Version` (**14**, Characters), tables `character_skills` and
  `character_forgotten_skills` (vmangos `characters.sql:199-204, 344-350`), with `ICharacterDataCleanup`.
- `EfCharacterSkillStore.ReplaceSnapshotAsync` deletes and inserts a character's rows in one transaction and answers
  false for a character that no longer exists. `SkillSaveCoordinator` is a single writer with latest-snapshot-wins
  semantics: a failed write is retained and retried and only a newer snapshot supersedes it; login and deletion wait
  for it; shutdown flushes it.

**World wiring** (`World/Skills`; slices `skills-feature`, `skills-trainer-reconcile`)
- `SkillsFeature`: `Skills:Mode = Retail` (default) reads the four DBC files. A configured file that is unreadable or
  has another layout **refuses startup**; with no file configured the daemon logs an error banner and keeps the legacy
  stand-ins, so existing hosts and tests behave as before. `Mode = Legacy` selects the stand-ins explicitly.
- Load order follows `Player::LoadFromDB` (`Player.cpp:14730-14977`): `SpellFeature` loads the spellbook and then calls
  every `ISpellbookLoadObserver`; the skills feature resets the free profession slots, loads the stored skills (which
  learn the spells they grant), then runs every stored spell through the learn path (`OnSpellLearned`). Removals of
  spells the load has not reached yet are ignored (vmangos reads the skills before the spellbook).
- `SpellbookCache` raises `SpellLearned`/`SpellForgotten`; the skills feature turns them into `OnSpellLearned`
  (a profession's first rank spends a free slot, `Player.cpp:3702-3709`) and `OnSpellForgotten` (`:3868-3880`).
- Spell effects (`SpellEffects.cpp` table 80-178): SKILL_STEP (`EffectLearnSkill`, `:2959-2977`), PROFICIENCY
  (`:2304-2321`), LANGUAGE, DUAL_WIELD, PARRY, BLOCK, and the no-op effects SKILL, TRADE_SKILL, WEAPON, DEFENSE, DODGE
  and SPELL_DEFENSE (`EffectNULL` / `EffectEmpty` in the reference). `Player.KnowsLanguage` follows the LANGUAGE effects
  once skills are attached (the race table remains for the legacy mode).
- Level-ups call `UpdateSkillsForLevel`; logout, a periodic per-map updater and shutdown enqueue changed snapshots.
- `CMSG_UNLEARN_SKILL` (`SkillHandler.cpp:59-70`: only the `UNLEARNABLE` row flag), GM `.setskill` and `.maxskill`
  (`CharacterCommands.cpp:826-899`).
- Trainers: `SpellSystemLearner` reads the real skill, the free slots and the profession flags of
  `GetTrainerSpellState` (`Player.cpp:4277-4297`), hides skills a `SkillRaceClassInfo` row marks not trainable
  (`:19548`), and buying casts the teaching spell so LEARN_SPELL and SKILL_STEP run (`NPCHandler.cpp:321-333`).

**Combat** (`Game/Combat/PlayerCombatSkills`; slice `skills-combat`)
- Weapon skill of the equipped usable unbroken weapon (Unarmed for an empty main hand, 0 for an empty off or ranged
  slot), defense skill (the maximum against players), the off-hand, parry and block predicates, all feeding the existing
  hit table through `CombatHooks` (`SpellCaster.cpp:116-152`, `Unit.cpp:499-509, 2498, 2515-2545`).
- Skill-ups after every resolved melee swing except evade; the victim rolls defense only while alive; none against
  players, none for a weapon skill while shapeshifted, none with a fishing pole (`Unit.cpp:8834-8846`,
  `SpellCaster.cpp:271-283`, `Player.cpp:5341-5410`).
- `CombatHooks` is unchanged for a player without attached skills (level x 5), so legacy hosts and every earlier
  combat test are untouched.

**Gathering** (`Game/Skills/GatheringRules`, `World/Skills/GatheringSpells`; slice `skills-gathering-spells`)
- OPEN_LOCK, OPEN_LOCK_ITEM and SKINNING casts and effects. `SpellSystem` gained the effect-check seam
  (`RegisterEffectCheck`) and resolves the GAMEOBJECT (23) and GAMEOBJECT_ITEM (26, new) implicit targets with the
  caster as carrier.
- `CanOpenLock` (`Spell.cpp:7869-7923`): key case, first fitting skill case decides, spell skill bonus, no skill when cast
  from an item. Range to the node centre, `ALREADY_OPEN`, `CHEST_IN_USE` at landing, `LOW_CASTLEVEL`, `TRY_AGAIN`.
- The orange failure (`Spell.cpp:6050-6057`, skinning `:5960-5963`): herbalism and mining never fail at the world maximum
  (`GetConfigMaxSkillValue` is a world constant, `World.h:744-748`), skinning likewise requires `skill < max`
  (`Spell.cpp:5962-5964`); only lockpicking can fail at the maximum (`canFailAtMax`, `Spell.cpp:6054`); the roll is
  `required > irand(skill - 25, skill + 37)`.
- One skill-up per player and node until the node respawns (`GameObject.SkillupSet`, `GameObject.h:171-176`); elite
  skinning doubles the chance (`SpellEffects.cpp:5371-5390`).

## Decisions where the references disagree (recorded once)

| Topic | Choice | Evidence |
|---|---|---|
| Mining and skinning skill-up decay | default **75** (retail decay), configurable to 0 | vmangos code default `World.cpp:710-711`, its comment `Player.cpp:5263`; the shipped sample configuration sets 0 (`mangosd.conf.dist.in:2842-2843`) |
| When the orange gather failure rolls | when the cast **lands**, not when it starts | `m_selfContainer` is set in `SetCurrentCastedSpell` (`Spell.cpp:3482`) after prepare's `CheckCast` (`:3403`); the roll needs it (`:6052`). The design draft proposed "at prepare"; the code says otherwise |
| `GetConfigMaxSkillValue` | world constant from the maximum player level, not per player | `World.h:744-748` (the design draft derived it from the player level; the review corrected it) |
| `UpdateSkillPro` return value | true on a missed roll | vmangos `Player.cpp:5334-5338` (mangos-classic returns false) |
| Weapon-skill combat curve | vmangos formula | `Player.cpp:5341-5410` |
| `SkillLineAbility` width | 15 canonical, 14 tolerated | `DBCfmt.h:68` in both references |
| `IsSpellFitByClassAndRace` masks | a `SkillRaceClassInfo` mask of 0 matches nobody in this function (but "any" in `GetSkillRaceClassInfo`) | `Player.cpp:19546` versus `DBCStores.cpp:589-602`; ported as written |

## Limits (not delivered, stated so nothing reads as done)

- **Crafting** (`CREATE_ITEM`, reagents, `UpdateCraft` callers, first aid and cooking acceptance paths) is not delivered:
  `PlayerSkills.UpdateCraft` and its formulas exist and are tested, but nothing casts a recipe yet. `SpellInfo` still has no
  reagent or equipped-item fields.
- **Key items** (skeleton keys, lockboxes opened with a key) need the item-cast path (`CMSG_USE_ITEM` casting with a cast
  item); locks that name a key never open. The item branch of OPEN_LOCK (a locked item as the target) works.
- **Veins** with several uses: the object system despawns an emptied chest, so a node is mined once per respawn
  (the `OnLootReleased` change belongs to the durable-loot lane). Fishing skill-ups (`UpdateFishing` exists) have no caller.
- Skill-ups from weapon-damage **spells** (`ProcSkillsAndReactives` with a `procSpell` requiring a weapon) need the
  spell item class, which `SpellInfo` does not carry.
- **Shapeshift** forms: no weapon-skill override for a form without weapons (the form byte of `UNIT_FIELD_BYTES_1` is
  read for the "no weapon gain while shapeshifted" rule, nothing writes it yet); **pets** (the owner counts as a
  player-controlled victim in vmangos).
- Derived stats (dodge, parry, block, crit percentages, defense bonuses) are not recomputed: nothing writes those fields in
  this tree. `PlayerSkills.SkillChanged` is the hook for their owner.
- `SkillAdded`, `SkillRemoving` and `SkillRemoved` have no subscriber: the MOD_SKILL / MOD_SKILL_TALENT auras (bonus
  re-application, `Player.cpp:5549-5560, 5627-5645`) and the quest-log clean-up on skill removal (`:5576-5599`) belong to
  the aura and quest areas.
- Trainer rows: the `SkillRaceClassInfo` minimum level is not applied (the reference applies it only when training; the
  list and the state share one predicate here). `spell_chain` `req_spell` and the custom `spell_chain` table are not loaded.
  Learning rank N without rank N-1 does not learn the lower ranks (`Player.cpp:3621-3629`), the spellbook owner's rule.
- Account trial restrictions (`HasTrialRestrictions`, `Player.cpp:5227, 5251`), battleground flags, the per-object use
  requirement table, the play-time flag and immune users are not modelled in gathering.
- Auto-unequip after losing a weapon skill mails an item that finds no bag space in vmangos; there is no mail system, so
  the item stays equipped.
- The database matrix (MariaDB, PostgreSQL) was not available here: the store tests ran on SQLite only.

## Acceptance

Automated: every formula, the field layout, the DBC readers, persistence, gating, trainer state, the effects, gathering
casts over a loopback session. The real-DBC probe `SkillDbcReaderTests.RealDbcProbe_*` runs when `ARCANECORE_TEST_DBC_DIR`
names a directory of build-5875 files (it reports Skipped otherwise, never a silent pass) and prints the field counts it
found; run it first to settle the SkillLineAbility width.

With a real 1.12.1 client (what only the client can show): the skills tab layout and the step titles, the temporary and
permanent bonus halves, the trainer window states and the confirm dialog, which spell the client casts on a node, the
cast bar and the "Failed" timing of an orange gather, the skill-up chat line (client side, from the field delta), weapon
skill numbers rising in combat, proficiency tooltips, and `.setskill 186 5 75` followed by a mining attempt.
