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
| Chat types the core does not serve (party, raid, guild, officer, channel, battleground) and pre-delivery vetting (ignore) | Implement `IChatMessageHandler.TryHandle(session, player, ClientChatMessage)` on an `IWorldFeature`. It is offered after the language checks and command parsing and before say/yell/emote/whisper/AFK/DND. The first handler that returns `true` consumes the message. Addon messages are offered without checks and are dropped if nobody takes them (vmangos). | `World/Handlers/ChatMessageHandlers.cs`, `ChatHandlers.cs` | `ChatHandlers.cs` |
| GM / player chat commands | Implement `ICommandGroup { IReadOnlyList<ChatCommand> Commands }` in `ArcaneCore.World`. Its roots are appended after the M6 builtins. A duplicate root name throws. | `World/Commands/ICommandGroup.cs` | `BuiltinCommands.cs` |
| Login / teleport packet stages | `LoginSequence.SendLoginPackets` / `SendInitialPacketsBeforeAddToMap` / `SendInitialPacketsAfterAddToMap` follow the vmangos split. A far teleport repeats the last two (vmangos `HandleMoveWorldportAckOpcode`). | `World/Handlers/LoginSequence.cs` | `CharacterHandlers.cs` |
| Database tables | Implement `IDataModule` in `ArcaneCore.Data`. It declares `Component` (Auth/Characters/World), `SchemaVersion`, the `SchemaChanges` (additive: `CreateTableChange` / `AddColumnChange`), `ConfigureModel(ModelBuilder)` and `AddServices(IServiceCollection)`. The contexts and `Add…Database` pick it up. Versions per component must be contiguous from 2. A gap or a duplicate throws before any database is touched. | `Data/Schema/DataModules.cs` | the three `*DbContext.cs`, `DataServiceCollectionExtensions.cs` |
| Test doubles in the end-to-end host | Implement `IWorldTestServices.Register(IServiceCollection)` in `ArcaneCore.World.Tests` (in your own folder). It is registered after the host's services. | `tests/ArcaneCore.World.Tests/WorldTestServices.cs` | `WorldTestHost.cs` |

## Schema versions

Each component's versions are a single contiguous sequence, so two branches that both pick
"the next version" will collide. **Ask the lead for a version** or take the next free number
and note it in `docs/integration/<area>.md`. On a collision the later PR renumbers. Current
state at this seam: auth v2 (inline, M6), characters v2 (inline, M6), world v1 (no steps).

## Local build and test (box)

```
export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build ArcaneCore.slnx -c Release -warnaserror
dotnet test ArcaneCore.slnx -c Release --no-build
```

MariaDB and PostgreSQL run on the shared box (`root`/`arcane` @127.0.0.1:3306,
`arcane`/`arcane` @127.0.0.1:5432) for the Data tests' engine matrix. See
`.github/workflows` for the environment variables CI sets.
