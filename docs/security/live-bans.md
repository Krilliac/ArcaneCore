# Live ban enforcement

Branch `claude/vw4-live-ban-enforcement` (base `claude/vw3-inbound-queue-cap`). Standing rule: retail
behaviour first, every deviation behind a `Bans:*` option that defaults to retail. References are
`D:\refs\vmangos` (primary), `D:\refs\mangos-classic`, `D:\refs\classic-db`, `D:\refs\wow_messages`.
Nothing was copied from them; behaviour is re-implemented and cited.

## What was missing

The brief assumed `account_banned` / `ip_banned` already existed in the hardening branch. They did not
(`hardening.md` listed them as not delivered). The only ban concept was the `Account.Status` column, which
had no expiry, reason, author or history, was checked only at new logon / new world authentication, and
never reached a connected session. This lane creates the tables, the store, the expiry logic and the
enforcement.

## Delivered

| Piece | Where | Retail reference |
|---|---|---|
| Tables `account_banned`, `ip_banned` (Auth schema `BanDataModule.Version`, 3 on this base) | `src/ArcaneCore.Data/Auth/BanDataModule.cs` | `sql/logon.sql:75-86`, `:139-146` |
| `IBanStore` + `EfBanStore` (permanent = `bandate == unbandate`, temporary until `unbandate`, `UNBAN: <msg>` audit row, history, listing, expiry purge) | `src/ArcaneCore.Kernel/Accounts/IBanStore.cs`, `src/ArcaneCore.Data/Stores/EfBanStore.cs` | `AuthSocket.cpp:464`, `World.cpp:2461-2486, 2639-2665`, `realmd/Main.cpp:213-215` |
| One shared decision point: `AccountBanEvaluator` (ban in force, effective status, address normalisation) | `src/ArcaneCore.Kernel/Accounts/BanModels.cs` | `AuthSocket.cpp:338-352, 464-476`, `WorldSocket.cpp:270-292` |
| `IAccountAdmin` (status override, session-key revoke, non-active lookup, usernames), separate from `IAccountStore` so other lanes' fakes keep compiling | `src/ArcaneCore.Kernel/Accounts/IAccountAdmin.cs` | n/a (ArcaneCore seam) |
| Post-commit `AccountStatusEvents` (a faulting subscriber never fails a mutation) | `src/ArcaneCore.Kernel/Accounts/AccountStatusEvents.cs` | `World.cpp:2469-2486` |
| Realm: IP ban -> `FAIL_NOACCESS 0x0D` before any SRP state; permanent -> `0x03`; temporary -> `0x0C` | `src/ArcaneCore.Realm/Net/LogonSession.cs` (optional trailing `IBanStore`) | `AuthSocket.cpp:338-352, 464-476` |
| World authentication: `AUTH_BANNED 0x1C` for an account ban row or an IP ban, fail closed on a store error, post-`Register` re-check that closes the status-read/Register race | `src/ArcaneCore.World/Net/WorldSession.cs` | `WorldSocket.cpp:333-345` |
| Live kick: a ban or status change disconnects the session (or every session from a banned address) through the normal close path, which saves the character; the banning author is skipped | `src/ArcaneCore.World/Bans/BanEnforcementFeature.cs` | `World.cpp:2469-2486, 2520-2570` (`LogoutPlayer(true)` + `KickPlayer`, author excluded at `:2552`) |
| Optional periodic re-check for bans written by other processes | `src/ArcaneCore.World/Bans/BanRecheckFeature.cs` | none (see deviations) |
| `.ban`, `.unban`, `.baninfo`, `.banlist` x `account`/`character`/`ip` | `src/ArcaneCore.World/Bans/BanCommands.cs`, `BanCommandText.cs`, `Kernel/Accounts/BanTime.cs` | `AccountCommands.cpp:516-1010`, `World.cpp:2500-2665`, `Util.cpp:197-275`, `Chat.cpp:2818-2945` |
| `arcane-account ban / unban / baninfo / banlist` | `tools/ArcaneCore.AccountTool/Program.cs` | n/a |

Texts are `mangos_string` 408-428 and 499 (`D:\refs\classic-db\Full_DB\ClassicDB_1_12_1_z2815.sql.gz`). The
multi-line IP entry (423) follows `mangos-classic sql/base/mangos.sql:3776` because the classic-db dump lost
its newlines. Security follows vmangos `Chat.cpp:170-191, 1022-1024, 1263-1266`: Moderator (ticketmaster) for
`baninfo`/`banlist` account and character, GameMaster for `ban account`/`character` and `baninfo`/`banlist ip`,
Administrator for `ban ip` and every `unban`.

## Configuration (`Bans:` section)

| Option | Default | Meaning |
|---|---|---|
| `Bans:RecheckIntervalSeconds` | `0` (off, retail) | Re-check connected sessions against ban rows, IP bans and the status column every N seconds (fractions allowed) |
| `Bans:RevokeSessionKeyOnBan` | `false` (retail keeps the key) | Null the stored session key after a live ban; the next world reconnect then answers `UnknownAccount` instead of `AUTH_BANNED` |
| `Bans:RejectUnparseableDuration` | `false` (retail) | Make a malformed `.ban` duration a syntax error instead of a permanent ban. A duration that overflows 32 bits of seconds (about 136 years) is always refused, by `.ban` and `arcane-account ban`, whatever this is set to: it never wraps into a short or permanent ban |
| `Bans:ProtectHigherSecurity` | `true` (stricter than retail) | Refuse `.ban account` / `.ban character` against an account whose security is equal to or higher than the invoker's (banning your own account still works). vmangos has no such guard; set `false` for exact parity. Not applied to `.ban ip` or to unbans |
| `Bans:RealmId` | `1` | Written to `account_banned.realm` (vmangos `realmID`); never filtered on, as retail |
| `Bans:MaxListedEntries` | `200` (stricter than retail) | The most entries one `.baninfo` history or `.banlist` reply prints before a "more entries exist" line; `.banlist character` also stops its per-account history queries there. Retail prints everything; `0` restores that |

Behaviour retail mandates (kick on `.ban`, refusal at logon and world auth, IP-ban refusal, the
author is not kicked by their own ban) has no switch.

## Deviations from retail (all documented, none silent)

* **The ban rows are the authority, the `Status` column stays an operator override** that is always honoured
  (existing tests and the Codex finding-4 tests are unchanged). Effective status: non-Active column wins, else a
  permanent row means Banned and a temporary one Suspended.
* **Live re-check** (`Bans:RecheckIntervalSeconds`) is an ArcaneCore extension. Retail never kicks for an
  externally written row: mangosd only reloads the IP cache every `BanListReloadTimer` and the account reload is
  commented out (`AccountMgr.cpp:317-327`, `World.cpp:697` default 60, `mangosd.conf.dist.in:232-234,418` say 120).
* **Events are in-process.** Only a ban written by this world process (`.ban`, the stores) kicks instantly. The
  realm daemon, `arcane-account` and raw SQL are separate processes; their bans apply at the next login or, with the
  re-check on, within one interval. `arcane-account ban` says so when it runs.
* **Fail open for live sessions, fail closed at authentication.** A store error during a re-check pass is logged,
  kicks nobody and the timer keeps running; a store error at logon or world auth closes the connection.
* World IP check reads the rows at authentication; retail checks a cached list refreshed on a timer (stricter, not looser).
* Realm check order: ArcaneCore validates the build and the username before the IP check, so an IP-banned client
  with a wrong build sees `VersionInvalid` (retail order for that pair was not verified).
* Ban times come from the application clock (`TimeProvider`), not the database's `UNIX_TIMESTAMP()`; hosts with
  skewed clocks see different expiry instants. Dates are 64-bit (retail `ip_banned` is `int(11)`, 2038 overflow).
* Schema: `BanId` identity primary key replaces `(id, bandate)` so two bans in one second do not both land
  (retail silently loses the second INSERT); the never-written `gmlevel` column is omitted; `ip_banned.ip` is
  `varchar(45)` (IPv6) and `banreason` `varchar(255)` where retail uses 32 and 50.
* A repeat ban of an address already actively banned keeps the first row (retail's second INSERT fails on the
  primary key while its cache is updated). A repeat ban of an account adds a row, as retail.
* `.banlist account` lists accounts with a ban **in force**; retail lists every account with `active = 1`, including
  temporary bans that expired but were not yet cleaned (cleanup runs at startup, at `.banlist ip`, and here on every `.banlist`).
* `.ban ip`: ArcaneCore keeps no `last_ip`, so it kicks live sessions by their connection address and always reports
  success; retail kicks accounts whose `last_ip` matches and prints `ip X not found` when none do (vmangos
  `World.cpp:2576-2579`). `.ban allip` is not implemented (needs `last_ip` and character level access).
* Ban/unban store faults answer "The ban database is unavailable; see the server log." (retail has no such text; its
  async holder fails silently).
* `.baninfo account`/`character` and `.banlist` of characters read the account name through `IAccountAdmin`; the
  `<hidden>` reason branch of vmangos is not implemented (it reads a `gmlevel` column no vmangos INSERT ever writes).
* `Bans:RequireNotHigherSecurityTarget` was designed and **not delivered**; retail has no hierarchy check on
  `.ban`, so (as retail) a GameMaster can ban an Administrator's account.
* Command security is vmangos' (see above), mapped from its 0-7 scale onto ArcaneCore's four levels; classic-db's
  all-level-3 `command` rows are not used.

## Operator guidance

* To enforce bans written by `arcane-account` or SQL on a running realm, set `Bans:RecheckIntervalSeconds` (tens of
  seconds on a large realm: each pass is a few indexed queries over the connected account ids).
* To make a ban survive a world reconnect with a stale key, set `Bans:RevokeSessionKeyOnBan`.
* Expired rows are purged at startup (`AuthDbInitializer`), at most hourly by the re-check, and by `.banlist`.

## Tests and what they do not prove

* Data: `BanStoreTests`, `AccountAdminStoreTests` (provider theories). MariaDB/PostgreSQL ran **nowhere** on the
  authoring machine; only SQLite did. The provider-specific paths (duplicate-key abort in a shared PostgreSQL
  transaction, MariaDB error 1062, implicit DDL commit between the two table steps, bool/bigint mapping) are written
  against real semantics (each `SaveChanges` owns its transaction, `BanIpAsync` re-reads and accepts a concurrently
  inserted active row) but are exercised on hosted CI only. The concurrent `BanIpAsync` theory is only meaningful there.
* Realm: `RealmBanEnforcementTests` over a real loopback `LogonSession`.
* World: `WorldAuthBanTests`, `LiveKickTests`, `BanRecheckTests`, `BanCommandTests`, `BanTextTests` against the real
  `WorldTestHost` (real sockets, real world thread, in-memory stores).
* No real 1.12.1 client was run: the client's banned/suspended/no-access screens are unverified.
* `arcane-account` has no test project; its logic is covered through `IBanStore` and one scripted SQLite run (create,
  ban, baninfo, banlist, unban, permanent ban); the CLI shell itself is not unit tested.
* Not delivered: `.reload account_banned/ip_banned` (the hot-reload coordinator is not on this base), per-realm
  filtering of `account_banned.realm`, `last_ip` and `.ban allip`.
