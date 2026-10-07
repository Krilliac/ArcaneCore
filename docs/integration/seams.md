# Integration seams (parallel delivery, 2026-10-02)

Several feature branches are being built at the same time, all targeting
`claude/friendly-hamilton-cuz4j4`. These seams let a feature plug in **without editing a
shared registration file**. Use them before you reach for an additive edit to a shared file.
If you do edit a shared file, keep the change minimal and additive, and record it in
`docs/integration/<area>.md` (file, what changed, why) so the lead can resolve conflicts.

All discovery is **reflection over one assembly**. It picks up non-abstract classes with a
parameterless constructor and orders them by full type name, so the order is deterministic.
Registering a duplicate fails at startup. Nothing fails silently (charter: fail closed).

| Need | Seam | Where | Shared file you no longer touch |
|---|---|---|---|
| Opcode handlers | Implement `IOpcodeHandlerGroup` (existing) in `ArcaneCore.World`. It is discovered. Registering an opcode twice still throws. | `WorldServiceCollectionExtensions.HandlerGroups` | `WorldServiceCollectionExtensions.cs` (the list) |
| A world service with lifecycle | `IWorldFeature { void Attach(WorldRuntime) }` in `ArcaneCore.World`. It is registered as a singleton (as itself and as each seam interface it implements) and attached before the world thread starts. Handlers get it with `session.Services.GetRequiredService<TFeature>()`. | `World/Features/WorldFeatures.cs` | `WorldServiceCollectionExtensions.cs`, `WorldHost.cs` |
| Login / logout hooks | `WorldRuntime.PlayerLoggedIn` is raised after the full login sequence (vmangos `HandlePlayerLogin` after `SendInitialPacketsAfterAddToMap`). `WorldRuntime.PlayerLoggingOut` is raised while the player is still in its map and in the online registry, before the save. World thread. A throwing handler is logged and does not stop the others. | `Game/Maps/WorldRuntime.cs` | `CharacterHandlers.cs`, `LogoutHandlers.cs` |
| Global world maintenance | Subscribe to `WorldRuntime.Updated(uint diffMs)`. Raised once per world tick after posted commands, map updates and unloads, even when no maps exist. World thread; each subscriber's exception is logged without stopping the other subscribers. Chat uses it to sweep expired session mutes once per clock second. | `Game/Maps/WorldRuntime.cs` | `World/Chat/ChatFeature.cs` |
| Chat types the core does not serve (party, raid, guild, officer, channel, battleground) and pre-delivery vetting (ignore) | Implement `IChatMessageHandler.TryHandle(session, player, ClientChatMessage)` on an `IWorldFeature`. It is offered after the language checks and command parsing and before say/yell/emote/whisper/AFK/DND. The first handler that returns `true` consumes the message. Addon messages skip language/command checks; by default they also skip mute/flood checks (vmangos), unless `World:Chat:AddonMuteAndFloodControl` is enabled. They are dropped if nobody takes them. | `World/Handlers/ChatMessageHandlers.cs`, `ChatHandlers.cs` | `ChatHandlers.cs` |
| GM / player chat commands | Implement `ICommandGroup { IReadOnlyList<ChatCommand> Commands }` in `ArcaneCore.World`. Its roots are appended after the M6 builtins. A duplicate root name throws. | `World/Commands/ICommandGroup.cs` | `BuiltinCommands.cs` |
| Login / teleport packet stages | `LoginSequence.SendLoginPackets` / `SendInitialPacketsBeforeAddToMap` / `SendInitialPacketsAfterAddToMap` follow the vmangos split. A far teleport repeats the last two (vmangos `HandleMoveWorldportAckOpcode`). | `World/Handlers/LoginSequence.cs` | `CharacterHandlers.cs` |
| Database tables | Implement `IDataModule` in `ArcaneCore.Data`. It declares `Component` (Auth/Characters/World), `SchemaVersion`, the `SchemaChanges` (additive: `CreateTableChange` / `AddColumnChange`; a `CreateTableChange` also creates the indexes the model declares on that table, so declare them with `HasIndex` in `ConfigureModel` and they reach upgraded databases too. `EnsureIndexesChange` repairs the indexes of an existing table and is for the context's own inline steps, see [index repair](schema-index-repair.md)), `ConfigureModel(ModelBuilder)` and `AddServices(IServiceCollection)`. The contexts and `Add…Database` pick it up. Versions per component must be contiguous from 2. A gap or a duplicate throws before any database is touched. | `Data/Schema/DataModules.cs` | the three `*DbContext.cs`, `DataServiceCollectionExtensions.cs` |
| Per-character data (starting items/spells, inventory/spell/quest load, character-list equipment) | Implement `ICharacterHooks` on an `IWorldFeature` (default interface methods: override only what you need). `OnCharacterCreatedAsync(session, character)` runs after the character row exists (vmangos `Player::Create` + `SaveToDB`); a throw answers CHAR_CREATE_ERROR. `OnPlayerLoadingAsync(session, character, player)` runs before the player is handed to the world thread (vmangos `Player::LoadFromDB`); a throw fails the login with CHAR_LOGIN_FAILED. `GetCharEnumEquipmentAsync(session, characters)` returns `CharEnumItem(displayId, inventoryType)` per slot (20: 19 equipment + first bag) for SMSG_CHAR_ENUM; first answer per character wins. Session task, may use the databases, never world state. | `World/Characters/CharacterHooks.cs` | `CharacterHandlers.cs`, `CharacterPackets.BuildCharEnum` |
| Character deletion (stored rows and live state) | Characters modules implement `ICharacterDataCleanup` (one transaction, may refuse; a guard test requires it of every characters module). Features with live per-character state implement `ICharacterDeleteHook` on an `IWorldFeature` (refuse, drain writes, drop caches after commit). See [character deletion](character-delete.md). | `Data/Characters/CharacterDataCleanup.cs`, `World/Characters/CharacterDeleteHooks.cs` | `EfCharacterStore.cs`, `CharacterHandlers.cs` |
| Per-map simulation (creatures, combat, game objects, AI, spell/aura ticks …) | Implement `IMapUpdater { Update(Map, uint diffMs); OnPlayerRemoved(Map, Player) }`. A system every map needs, in `ArcaneCore.Game`, takes `[DefaultMapUpdater(Order = n)]` and a `(Map, WorldRuntime)` constructor (may be internal); `WorldRuntime.GetMap` attaches it when the map is created (combat is Order 0). Anything else attaches itself on the world thread with `map.AddUpdater(system)` (creatures does this from its feature's `Install`). Updaters run in attach order at step (1c) of `Map.Update`, after the in-world packets and logout timers and before visibility/values/flush (vmangos `Map::Update`: players, then the grids' objects); a throwing updater is logged and the others still run. `OnPlayerRemoved` runs in `Map.RemovePlayer` after the player left the map's player list and before its visible set is cleared. Find a system with `map.FindUpdater<T>()`; give it a typed accessor with a C# 14 extension property in your own folder (as `map.Combat` in `Game/Combat/MapCombatExtensions.cs`). | `Game/Maps/IMapUpdater.cs`, `Game/Maps/DefaultMapUpdaters.cs` | `Map.cs` |
| Test doubles in the end-to-end host | Implement `IWorldTestServices.Register(IServiceCollection)` in `ArcaneCore.World.Tests` (in your own folder). It is registered after the host's services. | `tests/ArcaneCore.World.Tests/WorldTestServices.cs` | `WorldTestHost.cs` |

## Schema versions

### M13a lifecycle additions

`WorldRuntime.MapCreated` is raised on the world thread after a map is registered
and its default updaters are attached. `WorldRuntime.Maps` may be enumerated on
that thread (or before startup). A feature can install its updater on existing
maps during attachment and subscribe for future maps without editing a central
map registration list. Quest journals retain their state while a player is
temporarily detached during a far transfer.

`IWorldFeature.StopAsync` defaults to a completed task. The host stops the world,
then awaits discovered feature shutdown in reverse order, then drains the core
character save queue. All features receive shutdown even if another fails;
failures are collected and reported. The loopback host uses the same order.

These additive edits are in `Game/Maps/WorldRuntime.cs`,
`World/Features/WorldFeatures.cs`, `World/WorldHost.cs`, and the test host.
The quest login hook stages isolated player fields on the session task; its
world-owned registry is populated only after the normal login packet sequence.
The scoped implementation, visibility correction, and remaining adapters are
documented in [M13a](../../MILESTONE_M13A.md).

Each component's versions are a single contiguous sequence (a gap fails startup, see
`DataModules.Compose`), so a number cannot be reserved before the branch that uses it is
merged. **The lead assigns the final number at merge time, in merge order.**

- Keep your module's version in one constant and start with the next free number at your
  base. Note it in `docs/integration/<area>.md`.
- Durable chest loot (handoff item 4) is **characters 13** (`LootStateDataModule.Version`, `IntegratedSchemaTests` expects it
  through the constant). See [gameobjects-loot.md](gameobjects-loot.md).
- When another branch lands that number first, the lead renumbers your constant in the merge
  (only once your PR is out of draft, so it never races your pushes). Any test that asserts
  a literal current version should use `<Context>.Schema.CurrentVersion` instead.

The 2026-10-03 integration candidate assigns world v2 creatures, v3 maps,
v4 items, v5 spells, v6 quests/NPC; characters v3 items, v4 spells, v5 quests,
v6 social. Auth remains v2. See [fleet accounting](fleet-20261003.md) for the
exact source heads, schema lineage, and validation. These assignments apply to
this candidate; the original feature branches retain their draft allocations.

Current allocation after later integration: Auth 2, **Characters 3-10 modules, 11 (index repair), 12 (deletion outcome recovery), 13 (durable loot state)**, **World 2-8 modules, 9 (index repair), 10 (quest reputation reward columns)**; then the 2026-10-03 vanilla-wave integration added **Characters 14 (skills, `CharacterSkillsDataModule`), 15 (death/life persistence, `CharacterLifeDataModule`)** and **World 11 (player stats, `PlayerStatsDataModule`), 12 (creature behaviour, `CreatureBehaviourDataModule`), 13 (conditions, `ConditionsWorldModule`), 14 (on-kill reputation, `CreatureOnKillReputationWorldModule`)**; final Auth 2 / World 14 / Characters 15 after wave 1; wave 2 (2026-10-03) added **Characters 16 (item state, `CharacterItemStateDataModule`), 17 (talents, `CharacterTalentDataModule`), 18 (explored zones, `ExploredZonesDataModule`)** and **World 15 (totems, `TotemWorldDataModule`), 16 (pets, `PetWorldDataModule`), 17 (world state, `WorldStateDataModule`)**; final **Auth 2 / World 17 / Characters 18**. The repair steps are inline in `CharacterDbContext` / `WorldDbContext` (`IndexRepairVersion`). Modules may take numbers after the repair because their tables create their own indexes. Details, operator guidance and limits: [schema index repair](schema-index-repair.md), [character deletion](character-delete.md).

## Local build and test (box)

```
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build ArcaneCore.slnx -c Release -warnaserror
dotnet test ArcaneCore.slnx -c Release --no-build
```

MariaDB and PostgreSQL run on the shared box (`root`/`arcane` @127.0.0.1:3306,
`arcane`/`arcane` @127.0.0.1:5432) for the Data tests' engine matrix. See
`.github/workflows` for the environment variables CI sets.
