# Bounded build-5875 client acceptance handoff

The first useful real-client session can start with authentication, character creation,
world entry, a short walk and a fresh relog. It does not depend on clustering or broader
quest rewards. A second, small milestone exercises one ordinary creature-kill quest with
fixed/choice item and money rewards through the actual client UI.

## Revision and evidence

The qualified starting revision is
`485572b9849d4037f19bcde4135622b5001c7308` on
`codex/integrate-feature-fleet-20261003`, [draft #11](https://github.com/Krilliac/ArcaneCore/pull/11).
Its [push run](https://github.com/Krilliac/ArcaneCore/actions/runs/37100738845) and
[PR run](https://github.com/Krilliac/ArcaneCore/actions/runs/37100740557) passed all
8,864 tests across SQLite, MariaDB and PostgreSQL, with zero failures/skips and a
Release build with zero warnings/errors. The standalone mock passed 41 checks across
122 frames. These establish a productive starting point; client rendering/UI have
not passed acceptance.

Record the exact server SHA used in the session. An asynchronous settlement successor
must be pinned to its own qualified SHA and CI evidence before making latency claims.
The deliberately delayed database, uncertain-commit and stale-session cases belong to
automated qualification; this manual run uses the normal database path.

## What the separate session needs

- User-supplied file access to an actual WoW **1.12.1, build 5875** client, plus permission
  to run a disposable copy against a loopback server. No client files are currently
  assumed available. The user starts that session.
- The tested server checkout/binaries and .NET 10. No new database service is needed:
  use three newly created SQLite files. Do not point this procedure at valued databases.
- One client/account is sufficient for the first milestone. A second client/account is
  optional and gives the separate two-player movement/visibility observation.
- For the quest milestone, a developer-supplied build-5875 `FactionTemplate.dbc`, a
  client-valid item display ID, and the NPC greeting adapter described below. No assets
  are downloaded by this procedure. Server terrain extraction is optional for this
  short level path; no terrain/collision/swimming acceptance follows from a run without it.

## Disposable server setup

Run commands in PowerShell 7 on the same machine as the client. Set `$repo` to an isolated
checkout of the exact tested revision. If binaries are absent, build that checkout in
Release using `dotnet build "$repo\ArcaneCore.slnx" -c Release -m:1 -p:UseSharedCompilation=false --disable-build-servers`
and require exit zero; serialize that build with other desktop work.
All daemon/account commands below execute Release DLLs from this checkout, with their
working directory set to the new run directory. This is how they read this run's
`appsettings.json`; do not edit the repository's or another task's configuration.

```powershell
$repo = 'C:\path\to\qualified\ArcaneCore'
git -C $repo rev-parse HEAD
$run = Join-Path $env:TEMP ('ArcaneCore-client-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
New-Item -ItemType Directory -Path (Join-Path $run 'logs') | Out-Null
Get-NetTCPConnection -State Listen -LocalPort 3724,8085 -ErrorAction SilentlyContinue
$db = @{}
foreach ($component in 'Auth','Characters','World') {
    $file = Join-Path $run ($component.ToLowerInvariant() + '.db')
    $db[$component] = @{
        Provider = 'Sqlite'
        ConnectionString = "Data Source=$file;Mode=ReadWriteCreate;Pooling=False;Default Timeout=5"
    }
}
$config = @{
    Logging = @{ LogLevel = @{ Default = 'Information'; 'Microsoft.EntityFrameworkCore' = 'Warning';
        'ArcaneCore.World.Net.WorldSession' = 'Debug' } }
    Database = $db
    Auth = @{ BindAddress = '127.0.0.1'; Port = 3724; AutocreateAccounts = $false }
    Realms = @{ Seed = @(@{ Name = 'ArcaneCore Client Acceptance'; Address = '127.0.0.1:8085';
        Type = 'Normal'; Flags = 'None'; Population = 0.0; Category = 0 }) }
    World = @{ BindAddress = '127.0.0.1'; Port = 8085; TickIntervalMs = 50;
        AutosaveIntervalMs = 10000; LogoutDelayMs = 20000; PlayerCommands = $true;
        Motd = 'Disposable ArcaneCore client acceptance.';
        Maps = @{ DataDirectory = '' } }
    Quests = @{ OrdinaryRewardQuestIds = @() }
}
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'appsettings.json') -Encoding utf8
git -C $repo rev-parse HEAD | Set-Content -LiteralPath (Join-Path $run 'server-sha.txt')
Push-Location $run
$testPassword = Read-Host 'Password used only for this disposable account'
dotnet (Join-Path $repo 'tools/ArcaneCore.AccountTool/bin/Release/net10.0/arcane-account.dll') create CLIENTA $testPassword
if ($LASTEXITCODE -ne 0) { throw 'Account creation failed; inspect this disposable configuration.' }
dotnet (Join-Path $repo 'tools/ArcaneCore.AccountTool/bin/Release/net10.0/arcane-account.dll') list
Pop-Location
```

If the port check returned an existing listener, identify its owner and coordinate a
free test window; do not stop another task's server. Both endpoints here are loopback:
realm TCP **127.0.0.1:3724**, advertised world **127.0.0.1:8085**. A client on another
machine needs a separately agreed reachable address in both configurations; loopback
cannot reach the server on a different host.

Start the realm and world in two existing terminal windows. Set the same `$repo` and
`$run` values in each window, then use its respective command:

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

Fresh startup must report current schemas **auth 2 / characters 6 / world 6**, the realm and
world listeners above, and `World thread started (50 ms tick)`. Auth schema creation
may already appear in the account tool's console output, since that tool initializes
auth before creating the account. Restarts need not print a schema creation/upgrade
line when the current version is already present. An empty terrain
directory produces the expected heights/areas/liquids-unavailable message. A stock
world database contains start positions/race/class data but no quest/creature/item
content. `Loaded 0 quest templates` is expected for the first milestone.

In the user-started session, confirm the client's displayed build first. The session
owner can back up the disposable client's existing realm settings and point its
`realmlist.wtf` at `set realmlist 127.0.0.1`. Record that change and restore it after the
run. No installed client settings have been changed in preparing this handoff.

## First milestone: one client, about 15 minutes

| Action | Pass observation | Fail observation |
|---|---|---|
| Log in as `CLIENTA` | Named test realm appears; realm log says `CLIENTA` authenticated and realm list sent. | Incorrect-password/version error with known correct inputs, no realm list, or daemon fault. |
| Select the realm; create Human male Warrior `Clienta` | Character list contains the expected name/model/appearance. | Create failure, incorrect list fields or disconnect. |
| Enter Northshire | Character renders at map 0, zone 12, around (-8949.95, -132.493, 83.5312); remain connected for 60 seconds. World log says entered the world as `Clienta`. | Loading never completes, missing/incorrect player model, malformed-packet log or disconnect. |
| Walk/turn/strafe/jump a short level path near spawn | Controls respond, position changes and the client remains connected. Record the end position using `.gps` only if an authorized staff account was deliberately configured. | Freeze, repeated snap-back, unexpected disconnect or server map/packet errors. |
| Use normal client logout; wait for its 20-second countdown; fully log in again | Same character re-enters at the saved end position with unchanged identity and visible appearance. | Returns to initial spawn despite saved movement, missing character or stale-session failure. |
| Stop the world with Ctrl+C, restart the same DLL/config and relog | `World saved and stopped`; same saved character/location loads. | Shutdown persistence error or lost saved state. |

Do not require spells, starting equipment, loot, swimming or terrain collision for
this milestone. These observations give bounded client evidence for the exercised
authentication/creation/entry/persistence path. One client walking does not qualify
the two-client M4 visibility milestone.

Optional second-client check: create `CLIENTB` with the same account tool and a fresh
Human Warrior in Northshire. Each client must render the other, see its movement,
lose/reacquire it when it crosses the visibility boundary, and see it disappear on
logout. Record those observations separately.

## Ordinary reward UI milestone: prepare these prerequisites first

At the starting SHA, `QuestNpcInteractionHandlers` registers status/details/accept/
abandon/complete/request/choose, but does **not** register `CMSG_GOSSIP_HELLO` or
`CMSG_QUESTGIVER_HELLO`. The Game service already implements `GossipHello` and prepares
quest menus. Normal client right-click cannot currently reach that service. This is a
concrete adapter blocker, not a client setup problem. Add/qualify the strict eight-byte
NPC greeting adapter before scheduling quest UI acceptance; the mock's direct details
request does not prove the normal UI path.

The stock daemons also have no manual fixture/content-seeding CLI. The mock executable
offers only `self-test`: its 60-second deadline, ephemeral ports and automatic database
cleanup make it unsuitable for hosting this manual session. Its faction/display IDs
are synthetic and are not assumed to render in a real client.

Prepare one disposable content seed, before restarting the world, using the existing
schema and standard SQLite tooling (Python 3 `sqlite3` is available on this desktop):

| Table / configuration | Exact bounded content |
|---|---|
| `quest_template` | entry 900003; Method 2, Type 0, MinLevel/QuestLevel 1, title `Client reward acceptance`; creature objective `ReqCreatureOrGOId1=900030`, count 2; `RewOrReqMoney=1234`; fixed item 900040 x1; choice items 900041 x1 and 900042 x1; ordinary nonrepeatable, zero XP/spell/time/source/item/reputation/script/mail effects. |
| `creature_template` | Guide entry 900010 with `NpcFlags=2`, health 10, class 1; target entry 900030 with `NpcFlags=0`, health 1, class 1, no damaging AI. Use a client-valid display, such as verified Human display 49, and matching model radius/reach. |
| `creature_spawn` | Guide GUID 900020 at map 0, (-8948.95,-132.493,83.5312). Targets GUIDs 900021/900022 near (-8945.95,-132.493,83.5312) and (-8943.95,-132.493,83.5312), with 3600-second respawn intervals. |
| `creature_questrelation` / `creature_involvedrelation` | Both have `(id=900010, quest=900003)`, making the guide starter and ender. |
| `item_template` | Entries 900040/900041/900042, distinct acceptance names, class 15, quality 1, stackable 20, allowable class/race -1, no spell/quest effects; a display ID verified in the supplied client. Do not copy the mock's 900140/900141/900142 display IDs into a real-client assumption. |
| `Quests:FactionTemplateDbcPath` | Absolute path to the supplied build-5875 WDBC file: fourteen four-byte fields per record. Human faction-template 1 must resolve. Guide faction must resolve to a nonhostile NPC record whose Faction field is zero and contested-guard bit 0x1000 is clear; reputation-dependent NPCs are refused. Target faction must be attackable by the actual client. Record the verified IDs. |
| `Quests:OrdinaryRewardQuestIds:0` | 900003 only; use the reviewed synthetic definition above, not an arbitrary imported quest. |

There is no existing general quest importer to invoke. After the greeting adapter is
qualified, stop the world normally and save the following as `$run\seed-world.py`.
Supply a verified guide faction, hostile target faction and item display from the
user's build-5875 data. The script verifies the supplied faction records, inspects the
actual SQLite column names, fills unused numeric/text fields with zero/empty values,
and refuses to overwrite existing fixture entries. It touches only this disposable
world database. This recipe has not been executed against client files during
preparation; the separate session must retain its output and inspect the seeded rows.

```python
import pathlib, sqlite3, struct, sys
run = pathlib.Path(sys.argv[1]).resolve()
faction_path = pathlib.Path(sys.argv[2]).resolve()
guide_faction, target_faction, item_display = map(int, sys.argv[3:6])
assert run.name.startswith('ArcaneCore-client-') and (run / 'world.db').is_file()
assert item_display > 0
data = faction_path.read_bytes()
magic, count, fields, size, strings = struct.unpack_from('<4s4I', data)
assert magic == b'WDBC' and fields == 14 and size == 56
assert len(data) == 20 + count * size + strings
rows = [struct.unpack_from('<14I', data, 20 + n * size) for n in range(count)]
factions = {r[0]: r for r in rows}
assert len(factions) == count and 1 in factions
def hostile(npc, player):
    if player[1] and player[1] in npc[6:10]: return True
    if player[1] and player[1] in npc[10:14]: return False
    return bool(npc[5] & player[3])
guide, target, human = factions[guide_faction], factions[target_faction], factions[1]
assert guide[1] == 0 and not (guide[2] & 0x1000) and not hostile(guide, human)
assert hostile(target, human), 'target must be attackable by the Human client'
db = sqlite3.connect(run / 'world.db')
def insert(table, overrides):
    columns = db.execute('PRAGMA table_info("' + table + '")').fetchall()
    assert columns, table
    values = {c[1]: '' if c[2].upper() == 'TEXT' else 0 for c in columns}
    assert not (overrides.keys() - values.keys()), (table, overrides.keys() - values.keys())
    values.update(overrides)
    names = ','.join('"' + name + '"' for name in values)
    placeholders = ','.join('?' for _ in values)
    db.execute('INSERT INTO "' + table + '" (' + names + ') VALUES (' + placeholders + ')',
               tuple(values.values()))
with db:
    insert('quest_template', dict(entry=900003, Method=2, MinLevel=1, QuestLevel=1,
        Title='Client reward acceptance', Details='Defeat two nearby acceptance targets.',
        Objectives='Acceptance targets slain: 0/2', OfferRewardText='Choose one keepsake.',
        ReqCreatureOrGOId1=900030, ReqCreatureOrGOCount1=2, RewOrReqMoney=1234,
        RewItemId1=900040, RewItemCount1=1,
        RewChoiceItemId1=900041, RewChoiceItemCount1=1,
        RewChoiceItemId2=900042, RewChoiceItemCount2=1))
    insert('creature_model_info', dict(DisplayId=49, BoundingRadius=0.5, CombatReach=1.5, Gender=0))
    for entry, name, faction, flags, health in (
        (900010, 'Acceptance guide', guide_faction, 2, 10),
        (900030, 'Acceptance target', target_faction, 0, 1)):
        insert('creature_template', dict(Entry=entry, Name=name, MinLevel=1, MaxLevel=1,
            DisplayId1=49, Scale=1.0, Faction=faction, NpcFlags=flags, CreatureType=7,
            UnitClass=1, InhabitType=3, Civilian=1, SpeedWalk=1.0, SpeedRun=1.14286,
            MinLevelHealth=health, MaxLevelHealth=health, MeleeBaseAttackTime=2000,
            RangedBaseAttackTime=2000))
    for guid, entry, x in ((900020, 900010, -8948.95), (900021, 900030, -8945.95),
                           (900022, 900030, -8943.95)):
        insert('creature_spawn', dict(Guid=guid, Entry=entry, MapId=0, X=x,
            Y=-132.493, Z=83.5312, SpawnTimeMinSeconds=3600, SpawnTimeMaxSeconds=3600))
    insert('creature_questrelation', dict(id=900010, quest=900003))
    insert('creature_involvedrelation', dict(id=900010, quest=900003))
    for entry in (900040, 900041, 900042):
        insert('item_template', dict(entry=entry, name='Acceptance keepsake ' + str(entry),
            display_id=item_display, quality=1, **{'class': 15}, stackable=20,
            allowable_class=-1, allowable_race=-1))
print('Seeded quest 900003, one guide, two targets, three reward items.')
print('Faction/display IDs:', guide_faction, target_faction, item_display)
db.close()
```

Invoke it with actual verified IDs, then enable only this quest and configure the
supplied faction file. The placeholders below are session inputs, not guessed IDs:

```powershell
$factionFile = 'C:\path\supplied-by-user\FactionTemplate.dbc'
$guideFaction = Read-Host 'Verified nonhostile faction-template ID with Faction=0'
$targetFaction = Read-Host 'Verified faction-template ID hostile to Human template 1'
$itemDisplay = Read-Host 'Verified item display ID present in the supplied client'
python (Join-Path $run 'seed-world.py') $run $factionFile $guideFaction $targetFaction $itemDisplay
if ($LASTEXITCODE -ne 0) { throw 'Content seed failed; do not start the quest milestone.' }
$config = Get-Content -LiteralPath (Join-Path $run 'appsettings.json') -Raw | ConvertFrom-Json -AsHashtable
$config.Quests.FactionTemplateDbcPath = $factionFile
$config.Quests.OrdinaryRewardQuestIds = @(900003)
$config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $run 'appsettings.json') -Encoding utf8
Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $run 'seed-world.py'),$factionFile |
    Format-List | Out-File -LiteralPath (Join-Path $run 'content-checksums.txt')
```

Restart the world with the same daemon command. Require exactly one loaded quest
template and visible guide/targets before advancing. Preparing/qualifying the seed
and greeting adapter is the small remaining setup task for this milestone; the first
milestone need not wait for it.

Use a fresh `Clienta` character with an empty backpack and record initial copper.
Through ordinary UI actions:

1. Right-click the visible guide; require quest title/details and correct reward text.
   Accept; the log shows 0/2 kills. Abandon via the quest log, then accept again; exactly
   one active slot appears with reset progress.
2. Attack the two distinct targets through normal client controls. Require death
   animation and journal progress 1/2, then 2/2/complete. Misses may require further
   swings; do not manufacture credit with a database update or completion request.
3. Return to the guide. Require the actual offer window to show fixed item 900040,
   both choices, and **12 silver 34 copper**. Select the second choice, 900042.
4. Complete. Require exactly one fixed 900040 and one selected 900042, no 900041,
   copper increasing by 1234, and removal of the quest from the visible log. Verify
   item tooltips and completion text; missing icons/names or a broken choice window
   fail UI acceptance even when database values are correct.
5. Reopen the guide and repeat the available UI action; it must not grant again.
   Fully relog. Require both rewards and the same money total, with no active slot.
   Restart/relog once more if time permits; require the same durable result.

Query stores read-only after normal logout/flush, using the disposable `characters.db`:

```sql
SELECT Id, Name, Money, MapId, X, Y, Z FROM characters WHERE Name = 'Clienta';
SELECT quest, status, rewarded, timer, mob_count1, reward_choice
FROM character_queststatus WHERE guid = :character_id AND quest = 900003;
SELECT i.item_id, i.count, v.bag, v.slot, i.guid
FROM item_instance i JOIN character_inventory v ON v.item_guid = i.guid
WHERE v.guid = :character_id ORDER BY v.slot;
```

Expected reward row: status COMPLETE (1), rewarded true, count 2, timer zero, choice **item entry
900042**, not choice index 1. Inventory has one each of 900040 and 900042 and no
900041; exact GUIDs remain stable across relogs. Any extra grant, premature completion,
reward loss, stale journal slot, unexplained disconnect or persistence error fails
this bounded milestone. Malformed packets, full-capacity cases, delayed database
timing and uncertain commits stay in the independent automated tests.

## Evidence and closeout

The daemons have console logging, not a configured file sink. The commands above save
it to `$run\logs\realm.log` and `$run\logs\world.log`. Preserve `$run\appsettings.json`,
`server-sha.txt`, the three stopped SQLite databases, content seed/checksum, timestamps,
client build/provenance and screenshots of realm selection, world entry, journal
counts, offer choices, rewards and relog. If the client creates crash/error logs,
record their actual paths from the supplied installation; none are assumed present.
No raw session keys, account verifiers or proprietary assets are needed in a report.

Report each milestone as **PASS**, **FAIL**, or **BLOCKED**, with the first failing
action, visible message, matching daemon lines and database observations. Stop both
servers normally before archiving the databases; preserve the run directory for
review. Restore only this session's client realm setting. A pass does not establish
general playability, complete M13 quests, XP/reputation/mail/script rewards, loot,
terrain, broad combat or clustering acceptance.
