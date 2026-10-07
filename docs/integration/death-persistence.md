# Death and persistence (`claude/vw-death-persistence`)

Lane "death-persistence" of the vanilla-fidelity round. It makes the state that death and the
recovery from it depend on survive a logout: health, power and experience, the recent-death window,
and the ghost with its body. Behaviour follows vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`
(`D:\refs\vmangos`); every rule below cites the file and line it was read from. Nothing was copied
from the references; all code is a clean-room C# re-expression. Everything defaults to retail
behaviour; there is no deviation switch in this lane (the deviations that exist are limits, below).

## Delivered

### Seams and options (`src/ArcaneCore.Game/Death`, `src/ArcaneCore.World/Death`)
- `DeathOptions` (`World:Death`): `CorpseReclaimDelayPvP` / `CorpseReclaimDelayPvE`, both `true`
  (`src/mangosd/mangosd.conf.dist.in:2850-2851`). Only options that something reads exist.
- `DeathClock`: Unix seconds, the clock vmangos uses everywhere for death state (`time(nullptr)`,
  `Player.cpp:20190`, `MiscHandler.cpp:588`). `DeathHooks` registers options and clock per world,
  with the same `Register` / `TryRegister` / `For` pattern as `CombatHooks`.
- `DeathFeature` (auto-discovered `IWorldFeature`) binds the section and registers the hooks, and puts a
  logged-in ghost's body back (below).

### Wall clock instead of map uptime
`GhostTime` and `DeathExpireTime` used to be seconds of the current map's uptime counter
(`MapCombat.NowSeconds`), so a value compared on another map, or saved, meant nothing. They are Unix
seconds from `DeathHooks.Clock` now (`UnitCombat.GhostTime`, `UnitCombat.DeathExpireTime`).
`Death.CorpseReclaimDelay.PvP/PvE` are honoured in `GetCorpseReclaimDelay` (`Player.cpp:20184-20194`:
option off for the kind of death = always the first delay, 30 s) and `UpdateCorpseReclaimDelay`
(`Player.cpp:20197-20218`: option off = the window is not extended).

### Loaded phase (`ICharacterHooks.OnPlayerLoadedAsync`)
A second hook phase, run by `CharacterHandlers` for every hook after every `OnPlayerLoadingAsync`
and before the player reaches the world thread. vmangos restores health and power only after
inventory, spells, auras and `UpdateAllStats` (`Player.cpp:15057-15070`), so the maximums are final
when the stored values are clamped to them.

### Life persistence (characters schema module `CharacterLifeDataModule`)
- `CharacterLifeDataModule.Version` (= 15 after the 2026-10-03 vanilla-wave integration, after skills at 14; it was 14 on the lane branch; tests use the
  constant): tables `character_vitals` (health, power1-5, xp, death_expire_time, is_ghost) and
  `character_corpse` (map, position, orientation, ghost time, type; the map instance, `InstanceId`, since
  `CharacterCorpseInstanceDataModule`, characters v34 on the instances/death lane, vmangos `corpse.instance`). They mirror the vmangos
  `characters` columns `health, power1..power5, xp, death_expire_time`, the ghost flag, and the `corpse`
  table (`Player.cpp:16470-16476`, `14915-14917`), as separate tables so the shared `characters` row and
  its model are untouched; an absent row means "never saved with a life" and the fresh values stand.
- `CharacterState.Life` (`CharacterLife`) is filled by every `Player.CreateSnapshot`, merged by
  `CharacterSaveQueue.Merge` (the newest life wins), and written by
  `EfCharacterStore.StageStateAsync` in the same transaction as the rest of the snapshot, so a ghost
  flag and its corpse row commit together. `ICharacterLifeStore` is the read side. The module
  implements `ICharacterDataCleanup`.
- `CharacterLifeFeature` applies the life in the loaded phase: the death window capped at
  `now + 3 * 5 min - 1` (`Player.cpp:14915-14917`), experience through `PlayerProgression.InitializeLoadedPlayer`
  (clamped below the next level requirement), health and power never above the current maximums
  (`Player.cpp:15062-15070`). If the login auras (restored in `PlayerLoggedIn` handlers) raise the
  maximums, the clamped values are re-applied on the next world tick, but only for values the player has
  not changed since (`PlayerLife.ReapplyAfterAuras`).

### Ghost across a logout
- A spirit that logs out while still waiting at its body is released first, as vmangos does when the
  death timer is running (`WorldSession.cpp:694-701`: `BuildPlayerRepop` then `RepopAtGraveyard`):
  `MapCombat.OnPlayerLeaving` calls `RepopPlayer`. The snapshot taken after that holds the ghost, one
  health and the body at the death spot. `UnitCombat.Corpse` stays set after the corpse object is taken
  out of the map so the snapshot can read it.
- At login a ghost with a body gets its flag and dead state before it enters the map
  (`PlayerLife.ApplyGhostState`; vmangos gets them from the ghost aura, `Player.cpp:14972-14975`), and
  on the world thread `MapCombat.RestoreGhost` creates the corpse at the stored place (in the map it was
  left in), sets the stored ghost time, sets the "release timer" byte flag on a non-instanceable map
  (`Player.cpp:15434`), tells the client to water walk, applies the ghost form hook, and sends
  `SMSG_CORPSE_RECLAIM_DELAY` with the remaining delay (`Player::SendCorpseReclaimDelay(load = true)`,
  `Player.cpp:20228-20260`: nothing when the ghost time is after the window or the delay has passed).
- A character stored dead that never became a ghost, or a ghost without a body, comes back at half
  health and mana, rage empty (`Player::LoadCorpse` -> `ResurrectPlayer(0.5f)`, `Player.cpp:15434-15439`).

## Limits (explicit, nothing is stubbed behind them)
- Not delivered from the design: rest state persistence; bank slot persistence; the Map.dbc import. The
  graveyards, the ghost aura, durability loss on death, spirit visibility, ghost dungeon rules and resurrection
  requests were delivered later by the graveyards-resurrection lane (docs/areas/graveyards-resurrection.md);
  resurrection sickness (15007) is applied by the spirit healer only.
- Self-resurrection (effect 94) and the imported ghost-form spells run through the production World flow (the Codex continuation,
  [revival continuation](server-revival-20261004.md)); the resurrection requests are the graveyards-resurrection lane's
  `ResurrectionService`. The [death and item continuation](server-item-death-20261004.md) describes the death durability caller
  suppression, environmental single charging and current-pet revival.
- The body is not kept in the world while its owner is offline (vmangos keeps the corpse object in the
  world and saves it with the map). It is stored with the character and put back at the next login. Other
  players therefore do not see an offline player's body, and it does not decay to bones while its owner
  is away; the owner always finds it again.
- A body is restored into the instance it was left in (`Corpse.InstanceId`, kept when the logout takes the
  body out of its map). A row from before characters v34 reads instance 0: on an instanceable map it goes into
  the ghost's own map when the map ids match, as before v34. A dungeon body goes into its instance only when that
  map is loaded or a live instance save owns it (`IMapResolver.ResolveCorpseMap`, which creates the map the managed
  way). Otherwise the ghost keeps the body outside every map: no map is created for it (vmangos keeps such a corpse
  aside and never creates an instance map for one). Entering that dungeon still revives the ghost, and the spirit
  healer still works. Unlike vmangos, nothing adds the body to the map later; a deleted instance is never recreated
  and a live one gets its map at the restore.
- Experience is stored with the life, but rest bonus is not (`PlayerProgression` keeps it per session).
- The reclaim delay uses vmangos' two formulas as they are: `GetCorpseReclaimDelay` counts the window from
  the current time, the load-time packet counts it from the ghost time, so they can differ by one step.

## Schema allocation
Characters 15 in the integrated tree (`CharacterLifeDataModule.Version`; 14 on the lane branch). Tests refer to the constant and to
`CharacterDbContext.Schema.CurrentVersion`, never to the literal; `IntegratedSchemaTests` lists the module.

## Tests
`DeathHooksTests`, `DeathFeatureTests`, `LoadedPhaseTests`, `CombatDeathTests` (clock and options),
`PlayerLifeTests`, `GhostRestoreTests`, `CharacterLifeStoreTests` (SQLite always, MariaDB and PostgreSQL
with their connection strings), `LifePersistenceTests`, `GhostPersistenceTests`.

## References
- vmangos `src/game/Objects/Player.cpp`: 14915-14917, 14972-14975, 15057-15070, 15427-15440, 16470-16476,
  20184-20218, 20228-20260; `src/game/Server/WorldSession.cpp`: 694-701; `src/game/Handlers/MiscHandler.cpp`: 588;
  `src/game/Handlers/CharacterHandler.cpp`: 636; `src/game/SharedDefines.h`: 166-170;
  `src/mangosd/mangosd.conf.dist.in`: 2744-2760, 2849-2853.
