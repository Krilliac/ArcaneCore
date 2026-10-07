# Wave 3 review follow-ups, 2026-10-04

Continuation from `origin/main` at `7313b9eb089197b253dc51bd8bb5ceb803adcc6b`.
Worktree: `/workspace/arcanecore-work`; branch: `codex/wave3-escrow-loot-cleanup`.
The user requested parallel work across areas. The fixes below remain within the
integrated wave-3 systems; no new gameplay milestone or schema version is introduced.

## F8: escrow item loot cleanup

`EfEconomyStore.DeleteEscrowItem` stages removal of both `item_loot_state` and
`item_loot` after verifying that the item is ownerless. `EconomyCharacterCleanup`
does the same for the actual ownerless items discarded with nonreturnable mail
or unbid auctions. Both reuse `ItemLootPersistence.StageReplaceAsync` with an empty
replacement inside the existing transaction and `SaveChanges`.

Returned mail, auctions with a bid, unrelated escrow items, and items owned by a
different character retain their loot. No sweep of historical orphan rows is
performed; this change prevents new orphans on these deletion paths.

`EscrowLootCleanupTests` covers mail and auction deletion, idempotent retry,
save-failure rollback, integrity-conflict rollback, the owner guard, returned
mail and bid-auction preservation, character-deletion rollback, and stale
references to another character's owned item. Four cases failed on the original
implementation because loot state survived successful item deletion.

## F6 and F7: addon protection and expired mute storage

`World:Chat:AddonMuteAndFloodControl` defaults to false. Enabling it applies the
existing mute sources and shared spoken-chat flood counter before addon traffic
reaches a group, guild, channel or other chat feature. Staff retain the existing
counter exemption, and the message that trips a flood mute is delivered before
later messages are refused. No addon payload or packet layout is changed.

The default exemption is intentional: vmangos commit
`0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`, `ChatHandler.cpp:165-236`, explicitly
exempts `LANG_ADDON` from flood control and the mute check. See [chat rules and
reference](../areas/chat.md).

`WorldRuntime.Updated` runs once per tick on the world thread after commands,
map updates and unloads. It uses the existing exception-isolated event dispatch.
`ChatFeature` uses it to expire retained account mute entries once per clock
second, including disconnected accounts and a world with no maps. Active mutes
remain across logout and relog. Focused tests cover event frequency, callback
failure isolation, addon defaults/opt-in gating and offline/idle expiry.

F7's missing runtime `.mute` producer and the independent, unused
`ChatRestrictionService` flood API remain follow-ups. No mute persistence or
schema change is included.

## F9: character ban-list queries

`.banlist character` now requests only distinct account IDs whose characters
match the literal prefix, rather than loading every character identity. The
store compares CLR case variants at each prefix position using provider binary
collations, preserving supported accented Latin, Cyrillic and East Asian name
matching without SQLite's ASCII-only `UPPER` or accent-insensitive equality.
Prefixes longer than the existing name-column limit return no candidates before
constructing SQL predicates; cancellation is checked before that early return.
Ban history is checked in batches of 500 IDs and includes expired/inactive bans
and unban audit rows, as the existing command did.

(Superseded at the 2026-10-07 integration: the world keeps `Bans:MaxListedEntries` and the character directory prefix walk; the `CharacterListMaxResults` key described here no longer exists.) `CharacterListMaxResults` defaulted to `0` (retail unlimited). A nonzero
value is clamped to `1..500` and limits distinct candidate accounts before
checking history. One extra candidate detects truncation, and the command
always asks for a narrower prefix when candidates were omitted, including when
the retained candidates had no ban history. A narrower prefix can then locate
later banned accounts. `100` is a useful operator setting on a large realm.

The opt-in limit bounds transferred candidate IDs and history/name lookups; it
does not guarantee that many banned output rows or bound the underlying
database scan. Default query size remains unlimited for compatibility. No
schema version or name-normalization policy is changed. Tests cover owner
deduplication, ordering, literal wildcard characters, supported scripts,
history existence and the command's truncation behavior. See [ban options and
limits](../security/live-bans.md).

## Verification environment

The clean cloud workspace required a repository clone and .NET SDK 10.0.301,
matching `global.json`. Dependencies were restored without changing project files.
SQLite is available locally. MariaDB/PostgreSQL service tests and proprietary
DBC/world-dump probes require their configured external fixtures. Actual 1.12.1
client acceptance remains pending.

## Combined Release verification

All commands below completed with exit code 0 against the combined source,
including the final supported-script and oversized-prefix regressions:

```sh
dotnet build ArcaneCore.slnx -c Release --no-restore -m:1
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity minimal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
git diff --check
```

Build: **0 warnings, 0 errors**.

| Test project | Passed | Skipped |
|---|---:|---:|
| Cryptography | 8,017 | 0 |
| Data | 783 | 6 |
| Game | 3,659 | 1 |
| MockClient | 195 | 0 |
| Realm | 37 | 0 |
| World | 1,313 | 1 |
| **Total** | **14,004** | **8** |

The skipped tests need real DBC/world data or a configured read-committed
provider. MariaDB/PostgreSQL members of the provider theories were not generated
without their service connection settings; SQLite was exercised. The native
mock-client scenario separately passed **59/59** checks (build 5875, 148 frames).
No failing tests were rerun to obtain this combined result.

Logs: `/workspace/scratch/arcanecore-release-build.log`,
`/workspace/scratch/arcanecore-release-tests.log`, and
`/workspace/scratch/arcanecore-mock-self-test.json`.

Changes are local and uncommitted in the worktree named above. No push,
deployment, real-client acceptance claim, or database upgrade was performed.
