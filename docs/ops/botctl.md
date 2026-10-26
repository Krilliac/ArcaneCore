# Headless bot control (`.botctl`)

`.botctl` lets operators, scripts and AI agents drive playerbots directly from the server console, the control pipe or
[remote administration](remote-admin.md), so a feature can be tested in game without a client. Taking a bot over pauses its own AI: the
bot then does only what `.botctl` tells it. It acts through the same client opcodes a real client sends, so the server validates every
action, and **the server's answer is the command's result**: a refused cast, purchase or quest comes back as an error with the server's
reason.

* Reading verbs (`state`, `events`, `wait`, ...) need GameMaster. Verbs that drive a bot need Administrator.
* It is on whenever playerbots are on (`World:Playerbots:Enabled`). There is no extra switch.
* Output is text by default. Add `--json` to any verb for one JSON object per command, or `.botctl format json` once per session.

## Selecting bots

Most verbs take `<bots>` as the first argument:

| Selector | Bots |
| --- | --- |
| `Alice` or a bot id | One bot. A leading `$` is optional (`$Alice`, `$Ali*`), the form the other debug commands use for names. |
| `Ali*` | Every bot whose name starts with `Ali`. |
| `Alice,Bob,Carl` | A list (any selectors). |
| `all`, `running`, `controlled` (or `mine`) | All bots, the running ones, the ones under `.botctl` control. |
| `@team` | A named selection (`.botctl alias team Alice,Bob`). |
| `where:map=0&level=10-20&class=mage` | A filter. Keys: `map`, `zone`, `level` (n or a-b), `class`, `race`, `controlled`, `running`, `state`, `goal`, `name`. |

A selection over the bulk limit needs `--force`.

## Units

Where a verb wants a unit (`<unit>`): `self`, `target` (the bot's selection), `victim`, `attacker`, `corpse`, `nearest`,
`nearest:hostile|friendly|dead|player|any`, `npc:ENTRY`, `go:ENTRY`, `player:NAME`, `0xGUID`, `guid:N`, or a name (an online player, else the
nearest visible creature or object whose name contains it).

## Verbs

| Verb | What it does |
| --- | --- |
| `help [verb]` | Lists the verbs, or one verb's syntax. |
| `list [bots]` | Bots with running/controlled state, owner, map, level. |
| `take <bots> [passive\|defensive] [--start] [--force]` | Takes control. `defensive` attacks back whatever attacks the bot. `--start` starts a stopped bot under control. `--force` takes a bot a scenario is driving. |
| `release <bots>` | Hands the bots back to their own AI. |
| `mode <bots> passive\|defensive` | Changes how a controlled bot reacts on its own. |
| `stop <bots>` | Ends the current move or follow. |
| `move <bots> <x> <y> <z> [tol]` or `move <bots> <unit> [tol]` | Walks there with the bot's own navigation. Then use `wait <bots> arrived`. |
| `tele <bots> <map> <x> <y> <z> [o]` or `tele <bots> <unit>` | Teleports. |
| `follow <bots> <unit> [dist]` | Follows until stopped. |
| `target <bots> <unit>\|clear`, `attack <bots> [unit]`, `stopattack <bots>` | Targeting and auto-attack. |
| `cast <bots> <spell> [unit\|self\|at x y z]` | Casts. A cast the server ignores outright counts as error `ignored`. |
| `cancelaura <bots> <spell>` | Removes an aura from the bot. |
| `use <bots> <item> [unit]`, `equip <bots> <item>` | Uses or equips a carried item (by entry). |
| `interact <bots> <unit>` | Right-clicks: gossip, quest giver, vendor, trainer or battlemaster for creatures; use for game objects. |
| `gossip <bots> <npc> <index> [code]`, `menu <bots>` | Picks a gossip option. `menu` shows the menu the server holds open. |
| `quest <bots> hello\|query\|accept\|complete\|reward <npc> [quest] [choice]`, `quest <bots> abandon <quest>` | Quest giver actions. |
| `loot <bots> [unit] [--keep-open]` | Loots money and every item, then closes the window. The default unit is `corpse`. |
| `buy <bots> <vendor> <item> [count]`, `sell <bots> <vendor> <item> [count]`, `train <bots> <trainer> <spell>` | Vendors and trainers. |
| `say`, `yell`, `party`, `guild <bots> <text>`, `whisper <bots> <player> <text>`, `emote <bots> <id> [unit]` | Chat and emotes. |
| `invite <bots> <player>`, `accept`, `decline`, `leave` | Groups. |
| `bg <bots> join <battlemaster> <map> [group]`, `bg <bots> port <map> enter\|leave`, `bg <bots> leave <map>`, `bg <bots> status` | Battlegrounds. |
| `repop`, `reclaim`, `sit`, `stand` | Death and stand state. |
| `raw <bots> <CMSG_NAME\|number> [hex]` | Sends any client opcode (Administrator; debugging). |
| `state <bots> [--full]` | Position, zone, hp/power, combat, target, casting, motion task, last errors; `--full` adds auras, quests and inventory. |
| `quests`, `inventory`, `auras`, `path`, `errors [n]` | Parts of the state. |
| `events <bots> [since <seq>] [limit <n>] [kind <prefix>]` | What the server sent the bot and what it did. Sequence numbers are global, so pass the last one as `since`. |
| `stream <bots> on\|off [--errors]` | Pushes events to this console or RA connection as `#event` lines. |
| `wait <bots> <condition...> [timeout <s>] [--any] [--failonerror]` | Waits until the conditions hold (see below). The default timeout is 30 s. |
| `run <bots> <step> ; <step> ; ... [--continue]` | Runs verbs in order for the same bots and stops at the first failure. |
| `sleep <ms>`, `alias`, `format json\|text` | Helpers. |

### Wait conditions

Join several conditions with `and`.

* **Motion:** `arrived`, `idle`, `following`
* **Combat and life:** `combat`, `nocombat`, `alive`, `dead`, `dead <unit>`, `targetdead`
* **Resources:** `hp <op> <pct>`, `power <op> <pct>`
* **Quests and items:** `quest <id> none|incomplete|complete|failed|rewarded|taken`, `item <entry> [op] <n>`
* **Auras:** `aura <spell>`, `noaura <spell>`
* **Character and place:** `level [op] <n>`, `map <id>`, `near <x> <y> <z> [r]`, `near <unit> [r]`
* **Casting:** `casting`, `notcasting`
* **Server events:** `event <kind>`, `invited`

Operators are `<`, `<=`, `>`, `>=`, `=`, `!=`.

`wait ... arrived` fails early with `motion-failed` or `motion-lost` when the walk fails. `--failonerror` fails as soon as the server sends
the bot an error.

## Results

Text output prints one line per bot (`  Alice: ok ...` or `  Alice: error <code> ...`), then a summary line:

```
OK move (2/2 bots ok)
ERR not-controlled cast (0/1 bots ok): ...
```

With `--json` the command prints one object:

```json
{"ok":false,"verb":"cast","error":"server-error","bots":[{"bot":"Alice","ok":false,"error":"server-error","detail":"cast-failed: ..."}]}
```

When some bots succeed and others fail, the reason is `partial`. Over RA the same reason ends the command as `#done error <reason>`.

Common codes:

* **Selection:** `no-match`, `bad-selector`, `too-many`
* **Arguments:** `syntax`, `no-unit`, `no-item`, `no-quest`
* **Bot state:** `not-controlled`, `not-in-world`, `not-running`, `scripted-elsewhere`
* **Server's answer:** `refused` (the session refused the packet), `server-error` (the server's error answer), `ignored`
* **Waiting:** `timeout`, `motion-failed`
* **Sequences:** `step-failed`

## Scripted test example

```text
.botctl take Alice defensive --start
.botctl tele Alice 0 -8913.2 554.6 93.1
.botctl wait Alice map 0 timeout 20
.botctl move Alice npc:197 3
.botctl wait Alice arrived timeout 60 --failonerror
.botctl quest Alice accept npc:197 7
.botctl wait Alice quest 7 taken timeout 5
.botctl state Alice --full --json
.botctl release Alice
```

You can send the same thing as one line: `.botctl run Alice take ; move npc:197 3 ; wait arrived timeout 60 ; quest accept npc:197 7`.

## Client scripts for agents

* `tools/botctl/ra.py` (Python 3, no dependencies) logs in with the token and runs commands. Each result is one JSON line
  (`{"command":..., "ok":..., "reason":..., "lines":[...], "json":...}`). The exit code is 1 when a command failed.

  ```
  python tools/botctl/ra.py --token-file <live-home>\bin\ra-token.txt ".botctl take Alice" ".botctl state Alice --json"
  python tools/botctl/ra.py --token-file ... --script test.botctl --stop-on-error
  python tools/botctl/ra.py --token-file ... --stream Alice --seconds 60     # print #event lines
  ```

  As a module: `RaClient(token=...).run(".botctl state Alice --json")` returns a `Result`.
* `tools/botctl/RaClient.ps1` (Windows PowerShell 5 or later) does the same:
  `. tools/botctl/RaClient.ps1; $ra = Connect-Ra -TokenFile ...; Invoke-Ra $ra '.botctl list'`.

## How it works

`take` attaches a controller (`IPlayerbotController`) to the bot. While it is attached the bot's brain does not run. The controller:

* acknowledges teleports and movement orders like a client does;
* records every packet the server sends the bot as an event, keeping the last 512 events and the last 32 errors;
* runs one motion task with the brain's own navigation;
* in `defensive` mode, attacks back whatever attacks the bot.

`release`, a bot logout or a playerbot hot reload detaches it. Verbs run off the world thread and do their world work in world-thread
turns, so a long `wait` never stalls the world. The console, the control pipe and RA report a command done only when it has settled.