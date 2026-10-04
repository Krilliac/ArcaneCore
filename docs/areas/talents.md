# Area: Talents

Branch `claude/vw2-talents`. Vanilla 1.12.1 (build 5875) talent system: points, learning, respec, persistence.
Talent **effects** (spell modifiers, procs, stat auras) belong to the spell system and are not part of this area; the
startup report below says exactly how many talent rank spells that still leaves unhandled.

References (read-only, never copied): **vmangos** `D:\refs\vmangos` (primary; `src/game/Objects/Player.cpp`,
`Handlers/SkillHandler.cpp`, `Database/DBCfmt.h`, `Database/DBCStructure.h`, `Database/DBCStores.cpp`, `World.cpp`),
**mangos-classic** `D:\refs\mangos-classic` (where it differs), **wow_messages** `D:\refs\wow_messages`
(`wowm/world/spell/cmsg_learn_talent.wowm`, `msg_talent_wipe_confirm_*.wowm`, `cmsg_unlearn_talents.wowm`),
**classic-db** `D:\refs\classic-db` (spell 14867). Every formula below cites its source line.

## Delivered

| Layer | Files | What it does |
|---|---|---|
| Content | `Kernel/Talents/TalentCatalog.cs`, `Data/Talents/TalentDbcReaders.cs` | Immutable, fail-closed catalog read from the developer-supplied `Talent.dbc` (21 fields) and `TalentTab.dbc` (15 fields). |
| Rules | `Game/Talents/TalentRules.cs`, `RespecCost.cs`, `TalentOptions.cs` | Pure functions: points per level, learn evaluation in vmangos step order, free-point decision, respec price and decay. |
| Spell seams | `Game/Spells/SpellSystem.Unlearn.cs`, `SpellLearnObserver.cs`, `ISpellbook.ForgetSpell` | `RemoveSpell`, an ordered observer list on the learn path, default-interface `ForgetSpell`. |
| Service | `Game/Talents/TalentService*.cs`, `PlayerTalentState.cs`, `IRankChain.cs`, `TalentPackets.cs`, `TalentTrainerRules.cs`, `TalentEffectCoverage.cs` | Free points, `LearnTalent`, `ResetTalents`, disabled spells, login normalisation, wipe-confirm packets, effect coverage report. |
| Persistence | `Data/Characters/Talents/CharacterTalentDataModule.cs`, `World/Talents/TalentPersistence.cs` | `character_talent` and `character_spell_disabled`, a single-writer retain-on-failure queue. |
| World | `World/Talents/TalentFeature.cs`, `TalentHandlers.cs`, `TalentCharacterDeleteHook.cs` | Feature wiring, `CMSG_LEARN_TALENT`, `MSG_TALENT_WIPE_CONFIRM`, the unlearn gossip option, character-delete cleanup. |

### Behaviour (all cited)

- **Points per level.** `floor((level < 10 ? 0 : level - 9) * Rate.Talent)` (vmangos `Player.cpp:20500-20504`; rate default 1,
  `World.cpp:544`). 1 point at level 10, 51 at 60. Written to `PLAYER_CHARACTER_POINTS1` (0x44E, `UpdateFields.g.cs`):
  at login after the spellbook is loaded, on every `PlayerProgression.LevelChanged`, and after every learn or removal of a
  talent spell (`UpdateFreeTalentPoints`, `Player.cpp:3217-3247`). Used points are derived from the spellbook, never stored:
  the sum of `rank + 1` over every known talent rank spell (`DBCStores.cpp:466-480`, `Player.cpp:3697-3700`).
- **Learn** (`CMSG_LEARN_TALENT`, u32 talent, u32 zero-based rank, exactly 8 bytes, no reply packet) follows
  `Player::LearnTalent` (`Player.cpp:20684-20800`) step for step: free points, rank below 5, talent and tab exist, class mask,
  highest known rank, point delta (`requested - known + 1`, so ranks may be jumped), prerequisite talent (any rank index
  `>= DependsOnRank`), prerequisite spell, tier gate (`row * 5` points spent in the same tab), non-zero rank spell, not
  already known. Refusals are silent, as in retail; the reason is an enum for logs and tests.
- **Rank replacement and accounting** happen for every talent spell learn, wherever it comes from (client request, `.learn`,
  a spell effect), through `ISpellLearnObserver`: the old rank is removed (SMSG_REMOVED_SPELL) before the new one is announced
  (SMSG_LEARNED_SPELL), the free-point field follows, and a talent with a `SPELL_EFFECT_LEARN_SPELL` effect is cast triggered so
  it teaches its ability (`Player.cpp:3606-3622`, `:3709-3716`).
- **Respec** (`MSG_TALENT_WIPE_CONFIRM` from the client, trainer guid, exactly 8 bytes) follows `Player::ResetTalents`
  (`Player.cpp:4075-4147`): nothing spent returns false; the price is read, not-enough-money sends `SMSG_BUY_FAILED` (reason 2,
  item 0, vendor guid 0); the talent spells of the player's **own class** trees are removed (another class's talent spell stays,
  `:4101-4104`), after the auras of each rank spell's trigger spells (`:4108-4112`); money is charged, the multiplier advances
  (capped), the time is stamped (`:4132-4142`). The trainer then casts spell 14867 "Untalent Visual Effect" on the player
  (`SkillHandler.cpp:57`; mangos-classic `:63`).
- **Price.** First respec 1 gold, then multiplier x 5 gold up to 50 gold (`Player.cpp:4053-4073`). The multiplier decays one step
  per elapsed 30-day month (`Common.h:131`), floored at 2 once it had reached 2 (`:4028-4051`). Decay is always on for build 5875
  (vmangos gates it `!NoRespecPriceDecay || patch >= 1.11`, `:4056`).
- **Disabled spells.** Removing a talent removes it outright and also removes its `LEARN_SPELL` children and the higher ranks the
  player holds in the same spell chain: passive ones are removed, non-passive ones (the trainer-learned ranks of an ability talent,
  such as a second rank of Pyroblast) are *disabled*: out of the book, remembered in `character_spell_disabled`, and given back
  with `SMSG_LEARNED_SPELL` when the talent is learned again (`RemoveSpell`, `Player.cpp:3797-3885`; `LearnSpell` `:3768-3796`).
  Without this, a respec would leave the high ranks learned.
- **Gossip.** `GOSSIP_OPTION_UNLEARNTALENTS` (16) is offered when `Creature::CanTrainAndResetTalentsOf` holds (level >= 10, a class
  trainer, the player's class: `Creature.cpp:1523-1527`, `Player.cpp:12036-12039`). Selecting it closes the gossip and sends
  `MSG_TALENT_WIPE_CONFIRM` (u64 trainer guid, u32 cost) (`Player.cpp:12242-12245`, `:8259-8265`).
- **Login.** The respec state and the disabled set are restored; a spell that is both in the book and disabled loads as disabled;
  several ranks of one talent collapse to the highest; an overspend (more points spent than the level allows) resets the talents
  of a non-administrator and zeroes the free points of an administrator (`Player.cpp:3236-3243`). All of it is silent, before the
  self create.
- `CMSG_UNLEARN_TALENTS` is deliberately unhandled (vmangos `Opcodes.cpp:622` INVALID_PACKET; wow_messages lists it for versions 2 and 3 only).

### Startup report

`TalentEffectCoverage` lists every talent rank spell with an effect or aura type nothing handles
(`SpellSystem.HasEffectHandler` / `HasAuraHandler`). `TalentFeature` logs it once at startup. Learning such a talent is still
allowed and spends the point, as in retail, so this report is the measurable debt of the spell-modifier (aura 107/108), proc and
stat-aura work, not a hidden gap.

## Configuration (section `Talents`, class `TalentOptions`)

| Key | Default | Meaning |
|---|---|---|
| `TalentDbcPath`, `TalentTabDbcPath` | unset | Developer-supplied build-5875 files. Both unset: the feature is **inert** (no points, `CMSG_LEARN_TALENT` ignored, option hidden) and logs a warning. Only one set, or an unreadable or invalid file: startup fails. |
| `PointsRate` | 1.0 | vmangos `Rate.Talent`. |
| `RespecBaseCostGold` / `RespecMultiplicativeCostGold` | 1 / 5 | vmangos `Rate.RespecBaseCost` / `MultiplicativeCost`. |
| `RespecMinMultiplier` / `RespecMaxMultiplier` | 2 / 10 | vmangos `Rate.RespecMinMultiplier` / `MaxMultiplier`. |
| `RespecPriceDecay` | true | Always on in build 5875; a switch for older patches. |
| `IdempotentRespecDecay` | false | **Deviation switch.** vmangos mutates the stored multiplier on every price read without moving the time, so reading the price twice in a later month decays it twice. Default reproduces that; true applies one decay per elapsed month. |
| `EmptyConfirmCost` | `Current` | Cost field of the empty "nothing spent" confirmation: `Current` (vmangos) or `Zero` (mangos-classic). |
| `WipeRefusalAlsoSendsEmptyConfirm` | true | vmangos sends the empty confirm after any refused reset. Not verified against a real client. |
| `RequireClassTrainerForWipe` | true | mangos-classic requires a class trainer of the player's class (`SkillHandler.cpp:51`); vmangos accepts any reachable trainer. The gossip offer already enforces the class, so a real client never notices. |

Class trainers get `trainer_type` and `trainer_class` from imported `creature_template` metadata
(World schema v21), with `NpcServices:NpcTemplates` available as an override. The unlearn option
appears when a class trainer is interactable and the talent feature is active.

## Schema

Characters version **14** (one constant, `CharacterTalentDataModule.Version`; the integrator renumbers), two tables, both removed
by `ICharacterDataCleanup`:

- `character_talent (CharacterId pk, ResetMultiplier u32, ResetTimeUnix i64)`: vmangos `characters.reset_talents_multiplier` /
  `reset_talents_time` (`Player.cpp:14901-14902`, `:16452-16453`).
- `character_spell_disabled (CharacterId, Spell)`: vmangos `character_spell.disabled = 1`. A separate table so the spellbook
  table other features edit is untouched.

The learned talent spells are ordinary `character_spell` rows. SQLite is tested locally; MariaDB and PostgreSQL run through the
hosted CI matrix.

## Shared-file edits (small, additive; merge by hand)

| File | Edit | Why |
|---|---|---|
| `Game/Spells/SpellSeams.cs` | `ISpellbook.ForgetSpell` default interface method (`=> false`) | `SpellbookCache.ForgetSpell` already has the signature and satisfies it implicitly. |
| `Game/Spells/SpellSystem.Effects.cs` | `LearnSpell`: call the observers before the book add and after the passive cast | One path for trainers, quests, GM and spell effects. |
| `Game/Npc/QuestNpcServices.Gossip.cs` | `UnlearnTalentsOffered` property, visibility case, select case (shares the `Petitioner` close-and-raise branch) | Re-enable option 16 behind an owner predicate; unset = hidden as before. |
| `tests/.../SpellTestKit.cs` | `MemorySpellbook.ForgetSpell` | Test double. |
| `tests/.../IntegratedSchemaTests.cs` | One expectation line for the new module | The schema allocation test lists every module. |

## Limits and recorded decisions

- **Talent effects.** Spell-modifier talents (aura 107 / 108) are live since the spell-modifier engine ([spell-mods](spell-mods.md)):
  learning a rank casts the passive, the engine registers the modifier, a respec or a higher rank removes it. Talents that need procs,
  class scripts (aura 112) or stat auras the spell system lacks still do nothing here. Run the server and read the two coverage log
  lines (`TalentEffectCoverage`, `TalentModCoverage`) for the real numbers.
- **Level changes that bypass `GiveXp`** (GM `.level`, templates) do not raise `LevelChanged`; call `TalentService.InitTalentForLevel`
  from those paths, or the points catch up at the next login.
- **No `.reset talents` / `.modify talentpoints` commands.** A duplicate command root throws at startup, so the GM lane should call
  `ResetTalents(player, noCost: true)` and `InitTalentForLevel`. `CHARACTER_FLAG_RESET_TALENTS_ON_LOGIN` (offline reset) is not modelled.
- **Pets.** Respec does not remove the hunter pet and learning does not re-cast owner talent auras on it
  (`Player.cpp:4146`, mangos-classic `SkillHandler.cpp:35,66`); `TalentService.TalentsReset` and `TalentLearned` are the hooks.
- **No supersede packets** for trainer-learned ranks (`SMSG_SUPERCEDED_SPELL`): a disabled rank returns with `SMSG_LEARNED_SPELL` only.
- **Rank chain source.** vmangos walks `spell_chain`; this area walks SkillLineAbility.dbc forward links
  (`SkillLineRankChain`, via the NPC services' DBC path). They agree for trainer-learnable ranks. Without that DBC no higher rank
  is disabled (the talent itself is still removed) and the startup log says so.
- **Not atomic across stores.** A respec writes money (character row), the spellbook and the talent state through three independent
  queues. The disabled row is enqueued before the spell removal, but the queues have no cross-order, so a crash can lose an ability
  (spell gone, no disabled row) or leave both rows (loaded as disabled); it cannot grant a spell. A failed write is retained and
  reconciled at the next login or shutdown flush.
- **Quest-reward spells** commit through `SpellbookCache.AdoptCommitted`, bypassing `LearnSpell`; a talent spell granted by a reward would
  skip rank replacement until the next login normalises it. No 1.12 reward does this.
- **Hooks live on the `QuestNpcServices` instance** the feature attached to; a later rebuild of that instance must reapply
  `UnlearnTalentsOffered` and the `ForeignOptionSelected` subscription.
- **The learn observers run on the world thread only.** Login normalisation uses the book directly and is silent.
- **Hook order.** The talent login hook relies on running after the spell feature's hook (features and hooks are ordered by full
  name: `...Spells.SpellFeature` before `...Talents.TalentFeature`).

## Discrepancies between references

| Point | vmangos | mangos-classic / other | Chosen |
|---|---|---|---|
| Empty confirm cost | current price (`Player.cpp:8259-8265`) | 0 | `EmptyConfirmCost` = vmangos |
| Class check on wipe | none (`SkillHandler.cpp:37-56`) | `CanTrainAndResetTalentsOf` (`:51`) | `RequireClassTrainerForWipe` = true |
| Price decay | multiplier decays one step a month, mutated on every read | a different model: last cost + 5 gold steps, decaying 5 gold a month to a 10 gold floor, computed without mutation (`Entities/Player.cpp:3720-3745`) | vmangos model; `IdempotentRespecDecay` = false |
| Trainer visual spell | 14867 | 14867; wow_messages comment says 14876 | 14867 (classic-db has 14867, not 14876) |
| Unresolved prerequisite | silently satisfied (`Player.cpp:20727-20740`) | same | catalog load fails (charter: fail closed) |

## Real-client acceptance (written, not executed)

Needs a developer-supplied `Talent.dbc` and `TalentTab.dbc` (neither is in any reference tree) and, for the oracle test,
`ARCANECORE_TALENT_DBC_DIR` plus `ARCANECORE_TALENT_IDS_FILE` (one id per line; the 432 vanilla ids are the entries of
`cmsg_learn_talent.wowm` lines 9-464, for example
`sed -n 9,465p cmsg_learn_talent.wowm | grep -oE '[A-Z0-9_]+ = [0-9]+' | awk '{print $3}'`).

1. Start the server with both paths set; confirm the log shows the talent count, tab count and the coverage line.
2. Log in a level 10 character: the talent frame shows 1 point; level to 11, 2 points.
3. Learn a first-row talent: the point is spent immediately, no error text; the rank shows 1/n.
4. Try a second-row talent with fewer than 5 points in the tree: nothing happens.
5. Learn the next rank: the old rank leaves the spellbook, the new one appears; points drop by exactly one.
6. At a class trainer choose "I wish to unlearn my talents": the confirmation shows 1 gold. Confirm: the trainer plays the visual,
   the talents clear, the gold is taken. Repeat: 5 gold, then 10.
7. With less gold than the price: the client shows the not-enough-money message. **Check whether the extra empty confirmation
   ("you have not spent any talent points") appears or reads wrongly**; if it does, set `WipeRefusalAlsoSendsEmptyConfirm=false`.
8. With nothing spent: the empty confirmation appears.
9. Learn a talent that unlocks an ability, buy its second rank from the trainer, respec: both ranks leave the book; learn the
   talent again: both come back.
10. Relog after each step: points, spells and the price survive.
