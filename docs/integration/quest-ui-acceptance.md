# Quest UI acceptance with an owned build-5875 fixture

This is the next useful real-client session: open a nearby NPC's ordinary quest
menu, accept one supported quest, kill two targets, select an item reward, receive
the fixed item and copper, then confirm the durable result after a fresh relog.
It uses repository-owned synthetic content and normal realm/world daemons.

The preceding qualified integration stays pinned at
`f8ae6e8b5f805e94f145194a82f80e876fa2dec3`; its
[earlier client handoff](quest-client-acceptance.md) remains a historical procedure.
The separately qualified UI source is
`248accc71acbe70144b92c33761d8f9ae0ab07cc` on
`codex/quest-greeting-fixture-20261003`.
[Full source provider CI](https://github.com/Krilliac/ArcaneCore/actions/runs/37104390069)
passed Release with zero warnings/errors, all 8,978 tests and the 59-check native
scenario before canonical integration. Canonical publication is tracked in
[draft #11](https://github.com/Krilliac/ArcaneCore/pull/11). No actual client UI,
rendering or gameplay pass is claimed by this document.
The user's current acceptance target remains the preceding pin. This separate UI
follow-up is used only when the user chooses it in their selected file-access chat;
publishing it does not switch that session or its baseline instructions.
The [user-provided baseline run report](client-run-f8ae6e8-20261003.md) records
reported authentication, creation, entry and movement passes at that original
pin. Logout/relog and restart persistence remain pending. This coding task has
not inspected the run artifacts or resumed client work; NPC quest UI remains
unverified.

## Session inputs and bounds

The user starts the separate chat with file access to their WoW **1.12.1, build
5875** client when ready. Confirm the displayed version/build and the disposable
client copy's path there. Preparation here does not access client files, change
installed settings, download/extract assets or start that session.

Use one Human Warrior, level 1, on a newly exported fixture with an empty account.
The fixture grants player access to `.save`; it needs no GM elevation. Its ordinary
reward allowlist contains only **900003**. The supported reward is one fixed item,
one of two item choices and **1,234 copper**. XP, reputation, mail, scripts, spells,
source items, repeatable/timed/event/group/pet objectives and other NPC services
are outside this session. Server terrain remains optional for this short level
path; an empty terrain directory does not qualify height/collision/liquid behavior.

Both daemons and the client run on the same machine. Realm listens on
**127.0.0.1:3724** and advertises world **127.0.0.1:8085**. Coordinate a free port
window with other desktop work. The exporter creates a persistent disposable
directory; the session owns its lifetime, and graceful daemon shutdown preserves
its database/log evidence for review.

## Create the fixture once

Use PowerShell 7 and the Release binaries from an isolated checkout of the
qualified UI revision. Verify `git rev-parse HEAD` against the exact session pin
from the integration report. If the binaries are absent or built from another
revision, build that checkout with the existing .NET 10 SDK in an agreed desktop
window; do not reuse a binary merely because its filename matches.

```powershell
$repo = 'C:\path\to\qualified\ArcaneCore'
git -C $repo rev-parse HEAD
$run = Join-Path $env:TEMP ('ArcaneCore-client-' + [guid]::NewGuid().ToString('N'))
Get-NetTCPConnection -State Listen -LocalPort 3724,8085 -ErrorAction SilentlyContinue
$testPassword = Read-Host 'Password used only for this disposable account'
$fixtureOutput = dotnet (Join-Path $repo 'tools/ArcaneCore.MockClient/bin/Release/net10.0/arcane-mock.dll') `
    client-fixture --directory $run --account CLIENTA --password $testPassword
if ($LASTEXITCODE -ne 0) { throw 'Fixture creation failed; inspect its bounded error.' }
$fixtureOutput | Set-Content -LiteralPath (Join-Path $run 'export-output.txt')
$fixtureOutput
git -C $repo rev-parse HEAD | Set-Content -LiteralPath (Join-Path $run 'session-server-sha.txt')
Get-Content -LiteralPath (Join-Path $run 'appsettings.json')
```

`$run` must be a **new absolute path** with an existing parent directory. Fixture
account names and disposable passwords require 1-16 printable ASCII characters
without spaces. Let the exporter create it; an existing
directory is refused, preserving earlier runs. A failed export must be treated
as incomplete. Do not start the daemons until the command reports success and the
manifest/configuration are present. If either listener port is already occupied,
identify its owner and agree a test window rather than stopping another task's
process.

`client-fixture` uses the reusable `SyntheticQuestContent` definition to create
**auth.sqlite**, **characters.sqlite**, **world.sqlite**, a minimal
**FactionTemplate.dbc**, **appsettings.json** and **manifest.json**. It creates
`CLIENTA` with ordinary Player security and exits without starting a daemon.
The command's result identifies `Directory`, `ConfigurationPath`, `ManifestPath`,
`FactionPath`, `AuthDatabasePath`, `CharacterDatabasePath`, `WorldDatabasePath` and
`AccountName`. Retain `manifest.json` and `export-output.txt`: the manifest records
the build informational version and fixture definition version, with no password,
account verifier or session key. Use the explicit normal daemon instructions below.

The generated faction DBC is independently constructed **server data**. Leave it
inside the fixture and let `Quests:FactionTemplateDbcPath` point at it. It is never
installed into the client. The manual profile chooses guide faction-template
**35**, hostile target faction-template **14**, creature model **49**, and item
display **6418**, already referenced by the repository's item fixture. These IDs
enable a bounded candidate; actual client model,
reaction, icon and tooltip compatibility still require the observations below.

## Start the normal daemons and client

Use the generated configuration without applying the older handoff's ad hoc SQL
seed. Set the same `$repo` and `$run` in two existing terminal windows. Create the
log directory after successful export, if the exporter has not already created it:

```powershell
New-Item -ItemType Directory -Path (Join-Path $run 'logs') -Force | Out-Null
```

```powershell
# Realm window
Push-Location $run
dotnet (Join-Path $repo 'src/ArcaneCore.Realm/bin/Release/net10.0/ArcaneCore.Realm.dll') 2>&1 |
    Tee-Object -FilePath (Join-Path $run 'logs/realm.log')
Pop-Location
```

```powershell
# World window
Push-Location $run
dotnet (Join-Path $repo 'src/ArcaneCore.World/bin/Release/net10.0/ArcaneCore.World.dll') 2>&1 |
    Tee-Object -FilePath (Join-Path $run 'logs/world.log')
Pop-Location
```

The current working directory is how these standard hosts find this run's
`appsettings.json`. Require both loopback listeners and an active world thread.
Expected schema versions remain **auth 2 / characters 6 / world 6**; the exporter
may have already printed creation lines, so a restart need not print migrations.
Record the loaded quest count and terrain-availability message. Any schema,
content-loading, persistence or listener error blocks the session.

The shared authored content contains **three templates**. The guide initially
offers **900002, `Mock NPC quest`**, and **900003, `Mock combat reward`**. Only
900003 has the live 900030 targets and reward allowlist entry. The 900002 control
supports acceptance/abandonment but has no matching targets in this fixture.
Leave it unaccepted for this flow; it remains available after completing 900003
and can change the guide's later window without representing a second grant.

In the user-started file-access chat, back up the disposable client's realm
setting, point `realmlist.wtf` at `set realmlist 127.0.0.1`, and record the change
for restoration. No current installed client settings have been modified.
Log in as `CLIENTA`; create Human Warrior `Clienta`, enter Northshire and remain
connected for 60 seconds. Default Human start is map 0, zone 12, approximately
(-8949.95,-132.493,83.5312). Character creation/model/entry must pass before testing
the NPC UI. If an old client cache affects these custom item/quest IDs, use a
separately authorized fresh disposable copy and record that setup; preserve the
user's installed files.

## Bounded actual UI procedure

Record starting money and the empty backpack, then use only ordinary client
controls. A single client is sufficient; no packets are manually injected.

| Action | Required observation |
|---|---|
| Find the nearby `Synthetic guide` and both `Synthetic combat target` creatures. | All three models render, have readable names, and the guide is interactable while targets can be attacked. Manual-profile guide is 2 yards from the player's initial position; targets are 4 yards on either side. Use ordinary click/TAB/nameplate selection to distinguish targets. Record faction/nameplate colors. Invisible/broken models or inability to target are a content/client failure. |
| Right-click the guide while nearby. | The initial quest list shows `Mock NPC quest` and `Mock combat reward`. Select **900003, `Mock combat reward`**. Record its title/details, objective count and available reward entries. No silent click, endless window, unrelated gossip or disconnect. |
| Accept the reward quest. | Exactly one journal slot shows the quest with **0/2** targets. Close/reopen the guide and journal; neither action duplicates acceptance or creates kill credit. |
| Abandon through the journal, then right-click/reaccept. | The abandoned slot clears; reacceptance gives one fresh **0/2** entry. |
| Attack the first distinct target, then the second. | Real combat/death animations; progress **1/2** after the first, **2/2/complete** after the second. Misses may require further swings. A completion request or selecting the quest must not create credit. |
| Return and right-click the guide; choose the completed reward quest if a list appears. | Actual offer UI shows fixed **900040 x1**, choices **900041 x1 / 900042 x1**, and **12 silver 34 copper**. Item names/icons/tooltips render; no unexpected XP/reputation/spell reward. |
| Select the second choice and finish. | Receive exactly **900040 x1 + 900042 x1**, no **900041**; money rises by **1234** and the visible quest slot clears. Record completion text and bag slots. |
| Reopen the guide and repeat any available completion action. | No second grant, money increase or restored completed slot. The ordinary nonrepeatable quest cannot be accepted again. Other offered fixture quests do not count as a repeat of 900003. |
| Use normal logout and wait for its configured countdown; fully authenticate and relog. | Same character retains the two item counts and exact money total; 900003 remains absent from the active journal. |
| Stop the world normally with Ctrl+C, restart the same DLL/config and relog again. | `World saved and stopped`; the same durable rewards/money/journal state return. No persistence error or lost reward. |

`.save` is a permitted optional checkpoint, throttled for ordinary players every
20 seconds. Its chat acknowledgment does not by itself establish that an async
database queue has drained. Check durable rows after normal logout/graceful
shutdown. If a normal settlement is briefly pending, do not edit storage or grant
items manually to get past it; record any sustained freeze/error as a failure.

The eight-byte `CMSG_GOSSIP_HELLO` and `CMSG_QUESTGIVER_HELLO` adapters call the
existing authoritative Game interaction service. That service applies map,
visibility, distance, creature state and faction eligibility. Automated handler
checks cover exact lengths and eligible/denied responses; malformed-packet tests
stay in the mock suite. Neither adapter nor mock menu decoding proves that the
actual client opens and renders the desired UI.

## Durable checks and report

After normal logout or stopped daemons, open only the disposable `characters.sqlite`
read-only with existing SQLite tooling. Resolve `:character_id` from the first
query and retain the results:

```sql
SELECT Id, Name, Money, MapId, X, Y, Z FROM characters WHERE Name='Clienta';
SELECT quest, status, rewarded, timer, mob_count1, reward_choice
FROM character_queststatus WHERE guid=:character_id AND quest=900003;
SELECT i.guid, i.item_id, i.count, v.bag, v.slot
FROM item_instance i JOIN character_inventory v ON v.item_guid=i.guid
WHERE v.guid=:character_id ORDER BY v.slot;
```

Require one rewarded row: status COMPLETE **1**, rewarded true, timer zero,
`mob_count1=2`, and `reward_choice=900042` (the item entry). Inventory contains
one each of 900040 and 900042, none of 900041, and stable GUIDs on fresh relogs.
Copper equals its recorded before value plus 1234. Inventory, money and rewarded
history must agree; an extra grant, loss or mismatch fails acceptance.

Preserve the manifest, exact server SHA, generated appsettings, command output,
content IDs/checksums, client build/path/provenance, time-stamped observations,
screenshots of menus/journal/rewards/relog, `$run\logs\realm.log` and
`$run\logs\world.log`, and the three SQLite files after graceful shutdown. If the
client produces error/crash logs, record their actual paths in the supplied copy.
Console logs are captured explicitly above; a daemon has no automatic file sink.
Restore this session's realm setting and leave the evidence directory intact.

Report each stage **PASS**, **FAIL**, or **BLOCKED**, including the first failing
action, visible message, matching daemon lines and durable observations. This
milestone establishes only the exercised ordinary quest UI flow. Delayed-store
latency, capacity, cancellation, ambiguous commit and stale-session guarantees
come from their automated qualification. It does not establish general
playability, full M13 quests, terrain, broader combat/rewards, or clustering.
