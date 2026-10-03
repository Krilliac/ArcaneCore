# Wave-2 integration release review (claude/vw2-integration @ fe4ebe1)

Independent, read-only review. Static reading only: no build, no test run (CI green on fe4ebe1 is taken as given).

Base: `origin/main` = 2e0a4c0 (`git merge-base HEAD origin/main`). The local `main` ref (49448fd) is stale: it is an
ancestor of 2e0a4c0, and diffing against it pulls wave 1 into the diff. Scope reviewed: 609 files, +61.8k/-1.1k.

## Verdict: APPROVE WITH FIXES

No blocker on the default configuration. Code hot-loading cannot be enabled by default or by an unauthenticated path.
One real correctness bug on the dev-runner path (finding 1) and one hardening gap in the module allowlist (finding 2) should be fixed or consciously accepted before release.

## Findings

1. MEDIUM (correctness, dev-runner/hot-code hosts only) - `src/ArcaneCore.World/Commands/CommandTableSource.cs:53` and `:75`.
   `TryAdd` and `TryReplace` build `new CommandTable([...])` without `current.Gm`, so the replacement table gets a fresh default
   `GmOptions`. `ChatCommands.Build` passes the bound `World:GmCommands` options (`ICommandGroup.cs:95`).
   Failure: with `World:HotCode:Enabled=true`, `HotCodeHost.StartAsync` appends `.hotcode`. From then on the live table
   ignores the configured `SecurityMap`, `ExactNameFirst`, `HideUnavailable`, `LowerSecurity` and `LookupMaxResults`.
   Example: an operator who set `ExactNameFirst=true` or a custom `SecurityMap` silently gets the defaults.
   `CommandTableSourceTests` never asserts `Current.Gm` is preserved, so the 12822 passing tests would not catch it.
   Fix: `new CommandTable([...], current.Gm)` in both places, plus a test with a non-default `GmOptions`.

2. MEDIUM (security hardening gap) - `src/ArcaneCore.World/HotCode/Modules/ModuleLoadContext.cs:22-31` vs `ModuleHost.cs:333-336`.
   The SHA-256 allowlist covers only `<name>.dll`. `ModuleLoadContext.Load` loads any other non-shared assembly the module
   references from the module folder (`File.ReadAllBytes`) with no hash check. If the allowlist is meant to defend against
   a folder that is not operator-only, a swapped private dependency runs unchecked with full server trust.
   Fix: either hash and check every dll the context loads against the allowlist (fail closed), or state in
   `docs/areas/code-hot-reload.md` that the allowlist covers only the entry assembly. The directory must be operator-writable only either way.

3. LOW (security posture) - `src/ArcaneCore.World/HotCode/HotCodeGuard.cs` (`IsDevelopmentLike`).
   "Staging" counts as development, so `World:HotCode:Enabled` and `World:HotCode:Modules:Enabled` are accepted there with
   no allowlist (the allowlist is required only when `AllowAnyEnvironment` lets code into a non-Dev/Staging host).
   Many deployments run Staging as production-like. Consider requiring the allowlist for Staging, or documenting it.
   Positive: the environment defaults to Production when `DOTNET_ENVIRONMENT` is unset.

4. LOW (security docs vs defaults) - `docs/security/hardening.md:104-105`; `src/ArcaneCore.Kernel/Configuration/AuthOptions.cs`, `WorldOptions.cs`.
   The connection caps (`MaxConnections`, `MaxConnectionsPerIp`) default to 0 (unlimited) on both daemons, so the new
   `ConnectionLimiter` does nothing unless configured. This is correct for retail fidelity and documented, but release notes
   must not describe it as active protection. The default-on protections are `World:MaxQueuedWorldPackets=8192`,
   `MaxQueuedWorldBytes=8 MiB` and `PreAuthTimeout=10s` (retail), and `Auth:MaxSessionDurationSeconds=300` (vmangos).
   The first two have no vmangos equivalent (they disconnect a flooding peer), so they are non-retail but default-on.
   The docs justify them as hardening; accept, but this is a deliberate exception to the "deviations default off" rule.

5. LOW (config default change) - `src/ArcaneCore.Realm/appsettings.json`: `AutocreateAccounts` true -> false.
   This is a security improvement and is documented (`docs/security/hardening.md:65`). README.md:104-120 and
   `docs/M1_ACCEPTANCE.md:31` still describe it as an enable-it step, so they are consistent. Existing dev setups that relied on
   the shipped `true` will now get "unknown account". Flag it in the release notes.

6. INFO - `scripts/dev-runner.ps1:308-317`. The dev runner enables `HotReload__Commands`, `World__HotCode__Enabled` and the
   modules lane through per-process env vars only, with an allowlist. Nothing in shipped `appsettings*.json` enables any of it
   (`HotReload:Commands=false`, `World:HotCode:Enabled=false`, `Modules:Enabled=false`, no `Allowlist` key, which is fine).
   The generated `dev-account.txt` goes to `<RunRoot>\ArcaneCore-dev-<name>`, outside the repo, with ACLs restricted by icacls.

## Checked and clean

- Schema allocation. Characters: 2 base ... 3 item, 4 spell, 5 quest-npc, 6 social, 7 reputation, 8 instances, 9 spell-state,
  10 economy, 11 inline index repair, 12 deletion, 13 loot, 14 skills, 15 life, 16 item-state, 17 talents, 18 explored zones.
  World: 2 creature, 3 map, 4 item, 5 spell, 6 quest-npc, 7 gameobject-loot, 8 creature-ai, 9 inline index repair,
  10 quest-rep, 11 player-stats, 12 behaviour, 13 conditions, 14 on-kill rep, 15 totems, 16 pets, 17 world-state.
  Versions are contiguous and unique per database. `DataModules.Compose` throws on a duplicate, and
  `IntegratedSchemaTests.FeatureModules_HaveAssignedVersions_AndDistinctTables` pins the full list, contiguity and table
  uniqueness. Note: `docs/integration/claude-handoff-20261003.md` is partly stale on the final numbers
  (the merge subjects say "World 16" for world-state while the constant is 17); the constants are authoritative.
- ICharacterDataCleanup. Every Characters-component module implements it (15 of 15, including the new item-state, talent
  and explored-zones modules, which delete their rows with `ExecuteDeleteAsync`). `CharacterDataCleanups.Uncovered` exists as a guard.
- DI. No duplicate registration of the same service type across all `AddSingleton/Scoped/Transient` sites
  (the only repeat, `IHotCodeWorld`, is `AddSingleton` in `AddHotCode` and `TryAddSingleton` in `AddHotModules`, so it is safe in either order).
  `HotCodeAudit` is likewise `TryAdd` in the modules path.
- Command roots. Duplicate roots across `ICommandGroup`s fail at startup (`ICommandGroup.cs:65`). `ReloadCommandTests:190`
  builds the full discovered table with reload enabled, and the other tests build it with it disabled, so there is no collision.
  `.reload`, `.hotcode` and `.hotmodule` are distinct roots; `.hotcode`/`.hotmodule` are Administrator and exist only when their host is registered.
- HotReload:Commands default false. When off, no coordinator is built and `ReloadCommands.IsEnabled` returns false
  (the root does not exist). A test covers both states.
- HotCode fail-closed. `HotCodeGuard.Enforce` runs in `Program.cs:41`, before the host is built and before any DB is touched.
  It refuses: a hot-reload-capable runtime with `Enabled=false`; `Enabled` outside Dev/Staging; modules without a Directory;
  modules outside Dev/Staging without `AllowAnyEnvironment`; and `AllowAnyEnvironment` on a non-Dev host without an Allowlist.
  No hot-code object is registered when the flags are off (`WorldServiceCollectionExtensions.cs:70-79`).
  The only entry points are Administrator-only chat commands (`AccountSecurity.Administrator`).
  Module names are validated by regex and by "directory directly under the root"; link folders and files are refused.
  The allowlist is fail-closed (missing, oversized, malformed or unlisted refuses; no skipped lines), re-read on every load,
  and checked against the same bytes that are loaded (`ReadAllBytes` once, then `LoadFromStream`; no TOCTOU).
- Hardening not weakened by merges. `ConnectionLimiter` and `AcceptLoop` are wired into both `LogonServer` and `WorldServer`
  (lease released in `finally`). Pre-auth timeout, inbound queue caps (counters released in `DiscardQueuedPackets`/`Release`),
  writer drain grace, one proof per challenge, degenerate SRP salt/verifier refusal (`Srp6Server.TryCreate`),
  challenge size/locale/length windows, `LogSafe.Escape` on account names, and the world-side account-status re-check
  are all present in the merged tree. There are no ban tables or IP-ban code in this tree; `AccountStatus.Banned`
  handling is intact on both daemons. IP bans remain unimplemented (the comment at `WorldSession` says so).
- Default-on non-vanilla behaviour. Every new `*Options` default sampled cites a vmangos value or is documented as retail;
  deviations are switches that default to retail. No default-on custom behaviour found beyond item 4.
- Leftovers. No `.dll`/`.exe`/`.log`/`.dmp`/`bin`/`obj`/scratch/`.env` files added. No secrets: the only credential-like
  strings are `arcane/arcane` in docs and appsettings (pre-existing dev defaults) and test-container commands in docs.
  No `TODO`/`FIXME`/`NotImplementedException` added in `src` (only `NotSupportedException` guards in the SQL migration reader, which are intentional fail-closed errors).
  One deletion, `CreatureMapSystem.Ai.cs`, is split into the `AI/` folder (the build is green).

## Process note

`main` in this checkout (49448fd) is behind `origin/main` (2e0a4c0). Anyone diffing against local `main` sees wave 1 as part of this
branch. The auto-memory note "main at 49448fd" is out of date.


## Disposition (integrator, 2026-10-03)

Verdict stands: APPROVE WITH FIXES; the blocking-class fix is applied, the rest are recorded as follow-ups.

| # | Finding | Disposition |
|---|---|---|
| 1 | `CommandTableSource` dropped `GmOptions` on add/replace | **Fixed** in this branch: both swaps carry `Current.Gm`; RED-first test `AddingAndReplacingRoots_KeepTheConfiguredGmOptions` failed before the fix, passes after. |
| 2 | Module allowlist hashes only the entry dll, not private dependency dlls | **Follow-up** (backlog: hash every dll the module context loads). Mitigation today: modules only load via an Administrator command, behind the fail-closed guard, and the dev runner enables them only per process. Not enabled in any shipped config. |
| 3 | Staging counts as development-like in `HotCodeGuard` | **Follow-up**: require the allowlist in Staging. Nothing enables hot code in shipped config, so no default exposure. |
| 4 | Connection caps default to 0 (unlimited); queue caps are a deliberate non-retail hardening default | **Release note**: do not describe the connection limiter as active until configured. |
| 5 | Realm `AutocreateAccounts` default changed to false | **Release note**: dev setups that relied on auto-create must opt in. |

Base note: the merge base is origin/main 2e0a4c0, not the stale local `main` ref.
