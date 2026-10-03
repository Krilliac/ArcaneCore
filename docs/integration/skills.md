# Skills and professions: integration notes

Lane `claude/vw-skills-professions`, based on `49448fd`. Behaviour, limits and references:
[docs/areas/skills.md](../areas/skills.md). This page is for the integrator: schema, shared-file edits, seams,
configuration, collisions.

## Schema

| Component | Constant | Value | Tables |
|---|---|---|---|
| Characters | `CharacterSkillsDataModule.Version` (`src/ArcaneCore.Data/Skills/CharacterSkillsDataModule.cs`) | **14** (next free after durable loot 13) | `character_skills (guid, skill, value, max)`, `character_forgotten_skills (guid, skill, value)` |
| World | none | | skills read the developer's DBC files, no world table |
| Auth | none | | |

The number lives only in that constant. `IntegratedSchemaTests` takes it from the constant (allocation table), the store
tests compare against it, and `ICharacterDataCleanup` is implemented by the module (character deletion removes both tables).
Renumber the constant at merge time; nothing else mentions 14.

## Configuration (`Skills` section, every default is the vmangos default)

```json
"Skills": {
  "Mode": "Retail",                  // Retail | Legacy
  "SkillLineDbcPath": ".../SkillLine.dbc",
  "SkillRaceClassInfoDbcPath": ".../SkillRaceClassInfo.dbc",
  "SkillTiersDbcPath": ".../SkillTiers.dbc",
  "SkillLineAbilityDbcPath": ".../SkillLineAbility.dbc",
  "AlwaysMaxSkillForLevel": false, "MaxPrimaryTradeSkill": 2,
  "GainCrafting": 1, "GainDefense": 1, "GainGathering": 1, "GainWeapon": 1,
  "ChanceOrange": 100, "ChanceYellow": 75, "ChanceGreen": 25, "ChanceGrey": 0,
  "MiningSteps": 75, "SkinningSteps": 75,
  "FlushDebounceMs": 5000
}
```

- All four paths or none. Some but not all, an unreadable file or another field count refuses startup.
- No path configured (the state of every existing host and test): an **error banner** is logged and the legacy
  stand-ins stay (`Player.Skills` is null, every skill reads 300, nobody dual wields). `Mode: Legacy` selects that
  explicitly. A `SkillCatalog` registered in DI is used instead of the files (the tests do this).
- The maximum player level comes from `Progression:MaxPlayerLevel`.

## Shared files edited (all additive, minimal)

| File | Change | Why |
|---|---|---|
| `Data/Npc/NpcServiceDbcReaders.cs` | SkillLineAbility reader accepts 15 fields, tolerates 14; new `ReadSkillLineAbilityRecords` | vmangos `DBCfmt.h:68` is 15 wide |
| `Game/Entities/Player.cs` | `KnowsLanguage` asks `Skills` when attached | LANGUAGE effects decide once skills exist |
| `Game/Items/PlayerInventory.Storage.cs` | `AutoUnequipWeaponsIfNeeded` | weapon skill removal |
| `Game/Npc/SpellSystemLearner.cs`, `World/Npc/NpcServicesFeature.cs` | optional skill catalog parameter; real skill reads, profession flags, not-trainable rule, teaching spell cast | trainers |
| `Game/Combat/CombatHooks.cs` | five default bodies consult `PlayerCombatSkills` only for a player with attached skills | hit table |
| `Game/Combat/MapCombat.Melee.cs` | one call after `DealMeleeDamage` in `AttackerStateUpdate` | skill-ups |
| `Game/Spells/SpellDefines.cs` | `SpellImplicitTarget.GameObjectItem = 26` | Pick Lock |
| `Game/Spells/SpellSystem.cs`, `SpellSystem.Targeting.cs`, new `SpellSystem.EffectChecks.cs` | effect-check seam (`RegisterEffectCheck`), GAMEOBJECT targets carried by the caster | gathering |
| `Game/GameObjects/GameObject.cs`, `GameObjectLocks.cs`, `GameObjectMapSystem.cs` | `SkillupSet`; optional `skillBonus` parameter on `CheckOpenLock` / `OpenLock` | one skill-up per node, spell bonus |
| `World/Spells/SpellbookCache.cs`, `SpellFeature.cs`, new `SpellbookLoadObserver.cs` | `SpellLearned` / `SpellForgotten` events, `ISpellbookLoadObserver` called after the book loads | skills follow the spellbook |
| `tests/.../WorldTestHost.cs` | optional `configureServices` parameter of `Start` | tests register a skill catalog |

Collision candidates with the other running lanes: `CombatHooks.cs` (faction-aware hooks: five default bodies, kept
byte-identical for players without skills), `SpellSystem.cs` / `SpellDefines.cs` (death-removes-auras: one hunk each),
`GameObjectMapSystem.cs` (durable loot: one parameter), `SpellFeature.cs` (one call site). The warrior-mechanics design adds
`SpellInfo` fields (`EquippedItem*`, reagents): nothing here reads them, to keep the two apart.

## Seams offered

- `ISpellbookLoadObserver` (`World/Spells`): implemented on an `IWorldFeature`, called after a character's spellbook (with
  its defaults) is loaded, before the player reaches the world thread.
- `SpellSystem.RegisterEffectCheck(effect, check)`: per-effect cast checks, strict at prepare, non-strict at landing.
- `PlayerSkills` events `SkillAdded`, `SkillRemoving`, `SkillRemoved`, `SkillChanged` (no subscriber in this lane: MOD_SKILL
  auras, quest-log clean-up and the stats owner attach here).
- `ISkillSpellHost` (Game): the spellbook owner's side of the skill rules.
- `Player.Skills`, `PlayerSkills.UpdateCraft` (the crafting owner calls it after a successful recipe).

## Tests

`tests/ArcaneCore.Data.Tests/Skills` (readers, catalog, rank chains, store on the engine matrix: SQLite here),
`tests/ArcaneCore.Game.Tests/Skills` (player model, formulas, field layout, trainer, combat, gathering rules and the
spell seams), `tests/ArcaneCore.World.Tests/Skills` (feature over loopback: login, persistence across logout, unlearn,
level-up, commands, trainer purchase, gathering end to end, the save coordinator).
