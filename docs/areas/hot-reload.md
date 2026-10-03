# Area: Live reload (`.reload`)

Branch `claude/vw-hot-reload`. Operator-initiated reload of configuration and content without a
restart, modelled on the vmangos `.reload` command tree. Nothing reloads by itself.

References (read-only, never copied): **vmangos** `src/game/Chat/Chat.cpp` (reload table 794-935,
root 1212), `src/game/Commands/ServerCommands.cpp` (config 1016, spell_template 1409-1417,
`reload all` 885-905), `src/game/World.cpp` (`LoadConfigSettings(reload)` 445-1100, `configNoReload`
3044-3055), `src/game/Spells/SpellMgr.cpp` (`LoadSpells` 3702-3750); **mangos-classic**
`src/game/Chat/Chat.cpp:942` (single administrator `reload` root).

## Delivered

**Pipeline** (`src/ArcaneCore.World/Reload/ReloadCoordinator.cs`, `src/ArcaneCore.Game/Reload`)
- `IContentReloadable` builds a `ContentCandidate` on a worker thread (read, build, never touching live
  state) and validates it there. Any exception, timeout (`HotReload:BuildTimeoutMs`) or validation
  problem ends the reload with live state untouched.
- The swap is posted to the world thread with `WorldRuntime.Post`, which `RunCommands` drains before any
  map updates (`WorldRuntime.cs` `RunTick`), so no map ever sees half a swap. It runs inside a
  `ReloadTransaction` (steps with paired undo actions): if a step throws, the earlier steps are undone
  and the result is `Failed ... rolled back`.
- A swap the world thread never picks up within `HotReload:CommitTimeoutMs` is cancelled for good; it
  cannot run later.
- One reload at a time (`Busy` otherwise). `ReloadResult` / `ReloadStatus` (Kernel) carry the outcome:
  `Applied`, `KeptCurrent`, `Rejected`, `Failed`, `Busy`.
- Reloadables are discovered (`IContentReloadable` in the World assembly) and registered by
  `ReloadFeature`; a new one is a new class, no registry edit.

**Commands** (`ReloadCommands`, Administrator, `HotReload:Commands` default on)
- `.reload config`, `.reload spell_template`, `.reload all` (every content reloadable, not the config,
  as vmangos `reload all`), `.reload status` (ArcaneCore addition), names matched exactly or by unique
  prefix (vmangos matches command words by abbreviation, `hasStringAbbr`).
- The command returns at once and reports from the world thread when the reload ends, so the tick is
  never blocked by a database read.

**`spell_template`** (`SpellContentReloadable`): `ISpellContentStore.LoadAsync` ->
`SpellStoreFactory.Build` -> swap `SpellSystem.Store`. The setter is now backed by a volatile field
(`SpellSystem.cs`); the store is immutable, so a reader sees the old table or the new one.
- Empty `spell_template` keeps the loaded store (vmangos `SpellMgr.cpp:3724-3732` returns before the
  first `LoadSpell`; the safe variant the critique chose over `item_template`, which clears first,
  `ObjectMgr.cpp:3814-3832`).
- Duplicate spell ids are rejected with the ids listed (the store is keyed by id).

**`config`** (`ConfigContentReloadable`, `WorldConfigKeys`): the configuration source list is rebuilt
into a throwaway configuration (the live root is never reloaded: a broken file would empty it), the
`World` section is bound to a candidate, and the live `WorldRuntimeOptions` object is updated in place
on the world thread, so every reader sees the new value on its next read.
- Live: `UpdateCompressionThreshold`, `AutosaveIntervalMs`, `CharactersPerRealm`, `Motd`,
  `ListenRange*`, `AllowTwoSideChat`, `AllowTwoSideWhoList`, `LogoutDelayMs`, `InstantLogoutSecurity`,
  `GmLevelInWhoList`, `PlayerCommands`, `Maps:GridUnload`, `Maps:GridCleanUpDelayMs`,
  `Maps:GridActivationDistance` (`GridContainer` reads the shared `MapOptions` at each use; grids
  already running keep their timer until it resets, like `MapManager::SetGridCleanUpDelay`).
- Live, social rules (`World:Social:*`, a configuration surface that did not exist before: `SocialOptions`
  was never bound from configuration; `SocialFeature` now binds it at attach and the reload keeps it
  current). Keys map one to one to vmangos: `AllowTwoSideGroup` = `AllowTwoSide.Interaction.Group`
  (World.cpp:612), `AllowTwoSideGuild` = `...Interaction.Guild` (:613), `AllowTwoSideChannel` =
  `...Interaction.Channel` (:611), `AllowTwoSideAddFriend` = `AllowTwoSide.AddFriend` (:618). All default
  to false, as vmangos. `AllowTwoSideChat` and `AllowTwoSideWhoList` stay under `World:` where they
  already lived (`...Interaction.Chat` :610, `...WhoList` :617). vmangos' other `AllowTwoSide.*` keys
  (Accounts, Trade, Auction, Mail) have no ArcaneCore option and are not added here.
- Restart-only, reported as `<key> option can't be changed at reload, using current value (<v>).`
  (vmangos `configNoReload`, World.cpp:3044-3055): `TickIntervalMs`, `Maps:DataDirectory`, `Port`,
  `BindAddress` (the last two only when `IOptions<WorldOptions>` is registered, as in the daemon).
- A key removed from the file returns to its default (vmangos `GetIntDefault`).
- An unreadable or missing source, a value of the wrong type, or a negative interval/range rejects the
  whole reload. A test fails if a new `WorldRuntimeOptions` / `MapOptions` / `WorldOptions` property is
  not classified in `WorldConfigKeys`.

## Deviations from retail (all default to retail behaviour)

| Deviation | Switch |
|---|---|
| Reload builds off the world thread and swaps between ticks; vmangos reloads on the update thread and blocks it. Not observable to a client. | none (behaviour only) |
| A running aura keeps the `SpellInfo` it started with; vmangos mutates its spell table in place. Not observable to a client. | none |
| vmangos splits `reload` (`SEC_DEVELOPER`, Chat.cpp:1212) from `config`/`all` (`SEC_ADMINISTRATOR`, Chat.cpp:796,808); ArcaneCore's scale ends at Administrator so both are Administrator (cmangos-classic Chat.cpp:942). | none |
| `.reload status` and prefix matching of reloadable names are ArcaneCore additions. | none |
| `TickIntervalMs` is restart-only here; vmangos `MapUpdateInterval` is live (World.cpp:592-594). ArcaneCore's world thread captures its sleep once and `SpellFeature` its timer. | none |
| `.reload spell_template` does not reload `spell_mod` (no such table here; vmangos ServerCommands.cpp:1414). | none |
| The commands can be switched off. Retail has no such switch. | `HotReload:Commands` (default true = retail) |

## Limits (not delivered, by design of this slice)

- Only `config` and `spell_template` are reloadable. Items, creatures, quests, NPC data, gameobject
  loot, catalogs, maps and collision are separate slices (hr-items-lookup, hr-creatures, ...), several
  gated behind other lanes' work on the files they touch.
- No file watcher, no database revision polling, no automatic apply, no assembly (code) reload and no
  cluster rollout. The `HotReload` section itself is read when the world starts.
- Reloaded spells are visible to new casts, lookups and logins. Spells already in flight keep their
  record (see above). Clients cache spell data locally (WDB), so a client sees changed tooltips only
  after clearing its cache; the automated tests cover the server swap only, not a real client.
- The live host configuration (`reloadOnChange`) is untouched. A truncated `appsettings.json` written
  while the host watches it is handled by the host's own file provider (not by this feature); `.reload
  config` re-reads from the source list on demand and ignores a broken file.

## Tests

`tests/ArcaneCore.World.Tests/Reload` (coordinator, config, spell, command end-to-end over loopback) and
`tests/ArcaneCore.Game.Tests/Reload` (transaction). The three safety behaviours were proven load-bearing
by disabling each guard and watching its test fail: the empty-table guard, the restart-only message, and
the unreadable-source rejection.
