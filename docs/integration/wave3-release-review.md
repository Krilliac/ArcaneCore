# Wave 3 release review (claude/vw4-integration, head 8efe5d4 vs origin/main 3bda712)

Read-only review. No code was built or run; every finding below is from reading the merged tree. Provider
(MariaDB / PostgreSQL) behaviour is judged from the code and EF/driver semantics, not exercised.

## Verdict: APPROVE WITH FIXES

No blocker. Schema numbering is correct, the live-ban path disconnects live sessions and cannot be bypassed at
logon, the chat reconciliation is sound for everything the runtime actually reaches, and the db-upgrade dry run is
genuinely read-only. Four items should be fixed before release (F1-F4); the rest are low-severity or advisory.

## What was verified clean

- **Schema numbering.** Enumerated every `IDataModule` by component. World 2..20 contiguous (9 = inline index repair,
  18 GameObjectSpawn, 19 SpecialLoot, 20 StartAction). Characters 2..20 contiguous (8 = instance branch, 9 = spell
  state, 11 = inline repair, 19 ItemLoot, 20 Petition). Auth 2..3 (3 = BanDataModule). No duplicate version in any
  database. `IntegratedSchemaTests` asserts the module list, the `Max()` equals `CurrentVersion`, steps are
  `Range(2, n-1)` and the repair versions are not module versions.
- **ICharacterDataCleanup.** `ItemLootDataModule` and `PetitionDataModule` implement it. The item-loot cleanup reads
  `item_instance` by query before `SaveChanges` and the item module stages (does not execute) its delete, so ordering
  is safe. World modules hold no per-character rows.
- **Ban logon paths.** Realm refuses an IP ban before any account lookup or SRP state (`LogonSession.cs:161-172`) and
  an account ban after the status check. World auth checks status, ban rows and IP ban after the digest, registers,
  re-checks, only then initialises the header cipher (`WorldSession.cs:580-621`). Store errors propagate (fail
  closed). The status column stays an always-honoured override.
- **Live kick.** `EfBanStore.BanAccountAsync/BanIpAsync` publish after commit; `BanEnforcementFeature` resolves the
  session by account or by `RemoteAddress`, calls the thread-safe `Kick()`, and `Close()` posts the player removal and
  final save. Author is skipped as retail. Subscribers cannot fail the mutation.
- **MovementValidator order.** Validator drop, then `ApplyObserved`; acks go through the same `IsAcceptable`, check the
  GUID equals the player, and consume the pending-change counter only after validation. A dropped ack leaves the change
  pending, so the 4 s timeout re-enforces it (no kick, no unbounded growth: pushes are server-driven).
- **Duel clamp.** `ApplyDuelClamp` runs after the zero-damage return and before `combatLink`; clamp makes damage
  `health-1` so the `Health <= damage` kill branch is skipped, then `AfterClampedDuelDamage`; a non-opponent lethal hit
  takes `Kill` then `AfterLethalDuelDamage`. The `(Player)victim` casts are safe because the flag is only set for a
  player victim.
- **Mailbox access.** Retail `GameObjectMailboxAccess` is default (`MailboxAccess = Retail`); every mail entry point
  calls `CanUseMailbox`. Trade merge: slot range check precedes the indexer on all three entry points, accept
  re-validates gold and range, a modification always clears the partner's acceptance.
- **db-upgrade tooling.** `plan`/`status`/`check` never create the database (SQLite opened `ReadOnly`, `Exists` false
  -> `Missing`), never take the lock, and issue no write statement (grepped). Downgrade: `Newer` is a plan refusal
  (exit 4) and `SchemaDowngradeException`. `upgrade` plans every component read-only first, refuses all or none,
  gates on a backup, then `EnsureAsync` re-plans under the lock. The planner nested open/close of the context's
  connection is ref-counted against `SchemaBootstrapper`'s outer `OpenConnectionAsync`, so the session-level
  GET_LOCK / `pg_try_advisory_lock` is not dropped by the planner. Lock-held probes use `IS_USED_LOCK` and `pg_locks`
  (read-only); the advisory key split into classid/objid is correct. Policy `Never` refuses before `CreateAsync`.
  `Database:Upgrade` binds through the already-registered `IOptions<DatabaseOptions>`.
- **Scratch / secrets.** No stray files in the diff (only `docs/`, `src/`, `tests/`, `tools/`, slnx, README); no
  `NotImplementedException`, `TODO` in code added, no credential in added lines. New `appsettings.json` carries the
  same `arcane/arcane` dev credentials as the other hosts.

## Findings

### F1 (Medium) SocialWriteQueue: `PetitionComplete` is a separate key carrying a stale guild snapshot
`src/ArcaneCore.World/Social/SocialWriteQueue.cs:207-213` (CompletePetition), `:378-391` (EnqueueWrite coalescing),
`:573` and `:599-607` (RetryOpaqueAsync); `src/ArcaneCore.Game/Guilds/GuildManager.Petitions.cs:24,31` and
`PetitionManager.cs:382`.

`CreateFromPetition` calls `AddMember` (which queues `SaveGuild(G)` under key Guild/G) and then `TurnIn` queues
`CompletePetition(guildSnapshotV0, petition)` under key PetitionComplete/P. Two ordering hazards follow, both without
any store failure:

1. A later guild change (MOTD, rank edit, invite accepted, **disband**) while the earlier `SaveGuild(G)` is still
   queued and not started merges into that earlier slot (key Guild/G, same epoch), i.e. it runs **before**
   `PetitionComplete`. `PetitionComplete` then upserts guild G from the older snapshot: newer data is overwritten, and a
   `DeleteGuild(G)` that merged in is undone (the guild row is recreated). The next normal save of that guild repairs
   a live guild, but a crash or a disband in that window persists the stale or resurrected guild.
2. If `PetitionComplete` fails all attempts it is retained; any later successful guild write triggers
   `RetryOpaqueAsync`, which replays the retained complete (old snapshot) after the newer write.

The window is the queue latency (milliseconds when idle, longer under load or a slow store). Fix: do not carry a
snapshot in a separate key. Either key the completion as Guild/G with an extra `PetitionToDelete` field in the payload
(newer guild writes then coalesce into it), or read the guild snapshot from the live guild at execution time. Add a
test: `SaveGuild(G)`, `CompletePetition`, `SaveGuild(G, newer)`, assert the store ends at "newer".

Related, same file `:221-231`: `PurgeCharacter` discards only retained `Row` keys, not retained `Petition` /
`Guild` writes of the character, so a retained `SavePetition` can resurrect a petition row after the purge. This
self-heals at next start (`PetitionManager.Load` deletes petitions whose owner is missing or already guilded), so it is
Low on its own.

### F2 (Medium) Ban commands do not compare target security
`src/ArcaneCore.World/Bans/BanCommands.cs:129,193` (`ResolveAccountAsync` returns `Security`, never used);
`:36-40` (`ban account/character` at GameMaster).

A GameMaster can ban or unban-lock any account including an Administrator and the realm owner, and `.ban ip` can
target an address shared with admins (the author's own account is skipped, others on that address are kicked).
vmangos does not guard this either, so it is parity, but this is the one place a compromised or rogue low staff account
turns into a lockout of the highest account. Recommend refusing when `target.Security >= invoker.Security` (config-gated
if strict retail parity is wanted).

### F3 (Medium) Ban duration parsing: silent permanent ban on typo, and uint wrap
`src/ArcaneCore.Kernel/Accounts/BanTime.cs:21-45` (unchecked `uint`), `BanOptions.cs:26-37`
(`RejectUnparseableDuration` default false).

Retail-faithful, but dangerous as a default: `.ban account X 1 spam` (digits, no unit) and any stray character returns
0, which is a PERMANENT ban. Separately the sum is `unchecked uint`: `100000d` wraps to about 579 days and a larger value
can wrap to 0 (permanent) or to a short ban, silently. Recommend (a) always reject overflow (compute in `ulong`/checked
and refuse > 10 years) regardless of the switch, and (b) default `RejectUnparseableDuration` to true or print the
resolved duration back (the reply already does via `SecsToTimeString`, so at least make the permanent case unmistakable
in the reply when the input was not well formed).

### F4 (Medium, design) Bans written outside the world process do not kick unless the re-check is enabled
`src/ArcaneCore.World/Bans/BanOptions.cs:26` (`RecheckIntervalSeconds` default 0); `BanRecheckFeature.cs:44`.

`.ban` in game disconnects at once. The operator tool (`arcane-account ban`), SQL and any other realm daemon write the
row but publish no event, so the player stays connected until they disconnect, and with `RevokeSessionKeyOnBan` false
they can also reconnect to the world with the old session key only if the ban check somehow passed (it does not: the
world auth re-reads the rows). So the gap is "stays online", not "can log back in". Retail behaves the same, but the
shipped tool prints a warning about exactly this. Recommend enabling a modest default (for example 30-60 s; one indexed
query per pass) or making the AccountTool say so every time it bans a name that has an active session.

### F5 (Low) Kick during world authentication is not detected
`src/ArcaneCore.World/Net/WorldSession.cs:614-624`.

`Kick()` only cancels `_kick`; `_state` becomes `Closed` in `Close()` after the read loop ends. The comment says the
session "may have been kicked while registered but not yet authenticated", but `_state == Closed` cannot be true at that
point. A ban event landing between `Register` and the cipher init does not stop the session: it sends `AuthResponse OK`,
the addon block and enters `CharacterSelect` before the cancelled token ends the loop. The re-read above it closes most of
the window, so impact is one answer to a banned client. Fix: also test `_kick.IsCancellationRequested`.

### F6 (Low) Addon-language chat bypasses mute and flood
`src/ArcaneCore.World/Handlers/ChatHandlers.cs:73-84`.

`Language.Addon` messages return before the mute and `UpdateSpeakTime` gates (default `AddonChannel = true`), so a muted
or flooding player can still broadcast to party/guild/raid/battleground/channel members without limit. Main had no flood
control at all, so this is not a regression; the new gates just do not cover the hole. Apply the mute check (and a byte
or count flood limit) to addon messages.

### F7 (Low) Dead mute seam and unexercised flood logic
`src/ArcaneCore.World/Social/ChatRestrictionFeature.cs:31-34`, `src/ArcaneCore.Game/Social/ChatRestrictionService.cs:39`,
`ChatFeature.cs:75`.

After the reconciliation nothing outside tests calls `ChatRestrictionService.Mute`, there is no `.mute` command and no
stored `mutetime`, so `IChatMuteSource` has no producer: the only mute that can ever fire is the in-memory flood mute.
The service's flood logic and its unit tests no longer run in the runtime (a green test of dead code). Also
`ChatFeature._sessionMutes` entries are removed only when that account is queried again (a slow, account-bounded leak).
Not a safety problem; track as "mute command missing" and prune the dead logic as the integration note says.

### F8 (Low) Item-loot sidecar rows are not removed with escrowed items
`src/ArcaneCore.Data/Economy/EconomyCharacterCleanup.cs:115`, `EfEconomyStore` (`DeleteEscrowItem`).

Mail and auction escrow items live in `item_instance` with `OwnerGuid = 0`. Their `item_loot_state` / `item_loot`
rows are not deleted when the escrow item is deleted (mail deleted or expired, auction removed, character deleted), so
orphan rows accumulate. They are harmless to correctness (guids are monotonic) and incidentally let generated loot
survive a mail round trip, but add the two tables to the escrow delete.

### F7b (Low) `.banlist character` is unbounded
`BanCommands.cs:363-380`: loads every character identity and then one `GetHistory` query per matching account, at
Moderator level. Prefix-filter in the store query and cap the result.

Follow-up implementation: the store now filters a literal case-insensitive prefix before selecting distinct owner
account ids, with binary per-position case comparisons for consistent extended Latin, Cyrillic and East Asian names
on all supported providers. History existence is queried in batches rather than fetching each account's full history.
`Bans:CharacterListMaxResults` enables a candidate cap (`1..500`), and a truncation notice asks for a narrower prefix
even if retained candidates have no bans. Its default is `0` (unlimited), preserving the standing retail-default rule;
operators must enable the cap to bound candidate results. This reduces query count and transferred data by default,
but does not bound database scanning or unlimited candidate lists.

### F9 (Low) Extra uncached pre-auth DB read per logon challenge
`src/ArcaneCore.Realm/Net/LogonSession.cs:161-172`. One more indexed read per unauthenticated connection, before SRP
(retail caches the IP list). The realm already did `FindByUsername` per attempt, so the amplification is x2 at most;
there is no per-IP connection throttle added or removed by this wave. Consider a short TTL cache for the IP ban lookup.

### F10 (Info) Items noted, no action required
- `SessionKeyRevoke` default off is safe: the world auth re-reads status and ban rows after the digest, so a banned
  account with a retained key is refused anyway.
- IPv6 bans are exact `/128`; a v6 client rotating addresses is not stopped by an IP ban (retail has no v6).
- `ChatFeature.GmWhisperingTo = 0` means plain players cannot whisper any staff account that has not run `.whispers on`
  (vmangos behaviour; the notice is "player not found"). Staff-support workflows should know.
- `RefuseActiveSessions` probes before the schema lock is taken (check-then-act); acceptable for an operator tool that
  documents "stop every daemon first", and `--allow-active-sessions` is explicit.
- The MariaDB `GameObjectSpawnDataModule` step issues two separate `ALTER TABLE` (implicit commits). It is resumable:
  `AddColumnChange` skips a column that already exists and the version row is written last. PostgreSQL and SQLite run
  the same step inside the schema transaction.
- Default-on behaviours checked for retail fidelity: duel boundary 50/40 yd (mangos), mail body 500 / subject 64 bytes
  and silent drop of oversize letters, 1 h item-mail delay, flood 10 messages / 1 s -> 10 s mute, trade scam prevention
  200 ms, `Mode=Retail` character creation. Each is documented with a vmangos source line; none is a non-retail default.
  Non-retail extras are all off by default (`SlimeDamage`, `VmangosChannelExtensions`, `RejectUnparseableDuration`,
  `RevokeSessionKeyOnBan`, `RecheckIntervalSeconds`, `MaxJoinedChannels`, `AllowDeleteWithAttachments`,
  `MailOversizeAnswersError`).

## Not verified here (needs hosted CI or a real client)

Provider SQL in `ServerProbe` (information_schema.processlist, pg_stat_activity, pg_locks), PostgreSQL behaviour of the
`uint` key columns of `item_loot`, and the whole "root-family GUID packing / duel boundary / slime" open-question list
in `wave3-integration.md`.

## Verdict and disposition

Verdict: **APPROVE WITH FIXES**, and the fixes below are done (RED-first, each with a test) on
`claude/vw4-integration`. Findings that stay open are listed in `wave3-integration.md`, section "Known follow-ups".

| Finding | Severity | Disposition |
|---|---|---|
| F1 `PetitionComplete` stale snapshot / replay / resurrected guild | Medium | **Fixed.** The completion is a write of the guild's own key carrying the petition id, so later guild writes merge into it (id carried over), a retained completion is superseded rather than replayed, and a disband is not undone. `PurgeCharacter` also discards the character's retained petition writes (retained guild writes stay: they are keyed by guild). |
| F2 Ban commands ignore target security | Medium | **Fixed.** `Bans:ProtectHigherSecurity` (default true) refuses `.ban account/character` against equal or higher security; own account still allowed; `false` restores vmangos parity. `.ban ip` and unbans are not covered (no per-address account list). |
| F3 Duration overflow / silent permanent ban | Medium | **Overflow fixed.** `BanTime.TryTimeStringToSecs` is checked; `.ban` and `arcane-account ban` refuse anything over 32 bits of seconds regardless of config. `RejectUnparseableDuration` stays default false (retail; flipping it changes command semantics): a typo is still a permanent ban, the reply says "permanently". |
| F4 `RecheckIntervalSeconds` default 0 | Medium (design) | Follow-up / release note. |
| F5 Kick during world auth undetected | Low | **Fixed.** The post-Register check also tests the kick token; a session kicked in that window gets no `AuthResponse OK`. |
| F6 Addon chat bypasses mute/flood | Low | **Opt-in protection added on 2026-10-04.** `World:Chat:AddonMuteAndFloodControl` defaults off, preserving the verified reference exemption. |
| F7 Dead mute seam, `_sessionMutes` leak | Low | **Expiry fixed on 2026-10-04.** Idle/offline mute entries are swept on world ticks. Missing runtime mute producer and unused independent flood APIs remain follow-ups. |
| F8 Orphan `item_loot` rows for escrowed items | Low | **Fixed on 2026-10-04.** Both loot sidecars are removed with actual escrow item deletions in their existing transaction; prior orphan rows are not swept. |
| F7b `.banlist character` unbounded | Low | **Filtering/batched history lookup fixed; cap opt-in.** `Bans:CharacterListMaxResults` bounds distinct candidate owners with a truncation notice; default `0` remains unlimited for retail compatibility (listed as F9 in the integration notes). |
| F9 Uncached IP-ban read per logon challenge | Low | Follow-up (listed as F10 in the integration notes). |
| F10 Info items | Info | No action. |

The [2026-10-04 continuation](wave3-followups-20261004.md) records the subsequent
implementation and combined verification separately from this original release review.
