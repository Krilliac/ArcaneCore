# Integration notes: Spells

Area: the core of the spell system (branch `feat/spells`). It is built on the seams in
[seams.md](seams.md) (PR #1, `feat/fleet-plan`).

## Schema versions claimed

| Component | Version | Module | Tables |
|---|---|---|---|
| world | **2** | `ArcaneCore.Data.Content.Spells.SpellWorldDataModule` | `spell_template`, `spell_cast_times`, `spell_duration`, `spell_range`, `spell_radius`, `playercreateinfo_spell`, `spell_target_position` |
| characters | **3** | `ArcaneCore.Data.Characters.Spells.CharacterSpellDataModule` | `character_spell` |

These were taken as the next free numbers at the seam (world v1, characters v2). **Collision
risk:** another area that also adds world or characters tables will pick the same numbers. The
lead assigns the final numbers. Renumbering means changing only the `SchemaVersion`
property of the module.

## Shared files edited

| File | Change | Why |
|---|---|---|
| `src/ArcaneCore.World/Handlers/LoginSequence.cs` | `SendInitialPacketsBeforeAddToMap` sends `SpellFeature.BuildInitialSpells(player)` (resolved with `session.Services.GetService<SpellFeature>()`). If the feature isn't registered it falls back to the old empty `CharacterPackets.BuildInitialSpells()`. Two `using` lines were added. | SMSG_INITIAL_SPELLS from the database (vmangos `Player::SendInitialSpells`). LoginSequence is not on the avoid list, and the seam has no other hook in this stage. |
| `tests/ArcaneCore.Data.Tests/M6StoreTests.cs` | `M5CharactersDatabase_UpgradesToVersion2` now asserts `CharacterDbContext.Schema.CurrentVersion` instead of the literal `2`. | Any characters `IDataModule` (here v3) raises the current version, so the upgrade test failed for every area. The intent (an M5 database upgrades to the current version) is unchanged. |
| `ArcaneCore.slnx` | One additive `<Project>` line for `tools/spell-import/ArcaneCore.SpellImport.csproj`. | Lets the DBC importer tool build in CI. |

Nothing on the avoid list was touched (WorldServiceCollectionExtensions, ChatHandlers,
CharacterHandlers, WorldRuntime, DbContexts, WorldTestHost).

## New folders (owned by this area)

- `src/ArcaneCore.Game/Spells/`: the world-thread spell engine (`SpellSystem`), packets, auras and seams.
- `src/ArcaneCore.Data/Content/Spells/`, `src/ArcaneCore.Data/Characters/Spells/`: rows, data modules, EF stores and the DBC reader/importer.
- `src/ArcaneCore.World/Spells/`: `SpellFeature` (IWorldFeature), `SpellHandlers` (IOpcodeHandlerGroup), `SpellCommands` (ICommandGroup), the spellbook cache and the store factory.
- `tools/spell-import/`: a console tool that imports the DBCs.
- `tests/**/Spell*`.

## Seams this area offers to other areas

| Need | Seam |
|---|---|
| Creatures as spell targets or casters | Set `SpellFeature.System.Units` to an `ISpellUnitResolver` that also finds creatures. The default `MapPlayerResolver` only finds players. |
| Real damage, healing, threat and death | Set `SpellFeature.System.Damage` to an `IDamageSink`. The default `HealthOnlyDamageSink` only clamps health, and leaves 0 health to combat. |
| Far teleports | Set `SpellFeature.System.Teleports` to an `ITeleportSink`. The default `NearTeleportSink` handles the same map only. It sends MSG_MOVE_TELEPORT_ACK. The client's ack handler belongs to the movement/map area. |
| New spell effects and auras | `SpellSystem.RegisterEffect(SpellEffectName, handler)` and `SpellSystem.RegisterAura(AuraType, AuraHandler)`. Call them from your own `IWorldFeature.Attach` after resolving `SpellFeature`. |
| Teaching spells (trainers, quests, items) | `SpellFeature.System.LearnSpell(player, spellId)`. It persists the spell, sends SMSG_LEARNED_SPELL, and applies passives. |
| Casting from other systems (items, procs, scripts) | `SpellFeature.System.CastSpell(caster, spellId, targets, triggered)` |
| Interrupting (combat damage, movement) | `SpellFeature.System.CancelCast` / `CancelChannel` |

## Requests to the lead

1. **Map update hook.** `SpellFeature` ticks from a timer that posts `Update` to the world thread
   once per tick interval, with at most one post pending. A `WorldRuntime` per-tick event (or an
   `IWorldFeature.Update(diff)`) would remove the timer.
2. **Per-login async load.** The spellbook (`character_spell`) is loaded once, in full, at startup,
   and then written through on an ordered background queue. This matches the CharacterDirectory
   precedent. A login-stage seam that can await I/O before the player is created would allow loading
   per character instead.
3. **Character deletion event.** Deleting a character leaves its `character_spell` rows behind.
   `SpellbookCache.DeleteCharacter(id)` is ready, but CharacterHandlers is on the avoid list.
4. **Character creation.** New characters get their `playercreateinfo_spell` defaults on first login,
   in `BuildInitialSpells`, rather than at creation (cmangos `Player::Create → learnDefaultSpells`).
