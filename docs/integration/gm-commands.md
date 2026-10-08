# GM commands (vanilla command set)

Lane `gm-commands` of wave 2, branch `claude/vw2-gm-commands`. Goal: the chat-command layer behaves like
vmangos for the commands a GM uses during acceptance (names, abbreviations, security levels, argument
grammar, reply texts, side effects). References are read-only checks against `D:\refs`; nothing was copied
from them. Reply texts come from mangos-classic `sql\base\mangos.sql` at the vmangos `LANG_*` id (vmangos'
base `mangos_string` rows are not in the reference tree; the one text vmangos changed that this lane uses,
id 50, is quoted from `sql\migrations\20240107103630_world.sql:14`). Each text is cited next to its member
in `src/ArcaneCore.World/Gm/Core/GmStrings.cs`.

**Nothing here has been checked with a real 1.12.1 client.** Shift-link rendering, the countdown banner and
the GM notification text are unit and loopback tested only (see "Client checks still pending").

## What is delivered

| Slice | Delivered | Files |
|---|---|---|
| gm-args | The vmangos argument grammar: `ExtractInt32/UInt32/Float` (+ `Opt` forms), literal, quoted (`' " [ ]`), `OnOff` (only on/off/ON/OFF), shift-links (`ExtractLinkArg`, `ExtractKeyFromLink`, link-type lists, the "something" field), `Htele` ids, `normalizePlayerName`, online `ExtractPlayerTarget`, `TimeStringToSecs`, `secsToTimeString` (`Chat.cpp:2690-3290,3364,3717`, `Util.cpp:197-282`) | `Gm/Args/*` |
| gm-core-a | `ChatCommand.RetailLevel` (vmangos 0-7) resolved through `World:GmCommands:SecurityMap`; `ICommandExtension` (several lanes contribute sub-commands to one root; a missing target or duplicate child fails at startup); `HasLowerSecurity` port; GM command audit log (`Chat.cpp:1908-1925`) | `Commands/CommandTable.cs`, `Commands/ICommandGroup.cs`, `Gm/Core/*` |
| gm-core-b + gm-builtin-retail | Retail table semantics: roots in vmangos order, first-prefix abbreviation (`hasStringAbbr`), resolve then authorise ("This command is not available to you."), retail help / `.commands` / sub-command list / syntax texts, retail levels for the pre-existing commands, retail `.gm`, `.gm chat`, `.kick`, `.save`, `.announce`, `.notify`, `.modify money` | `Commands/*`, `Gm/Core/Retail*.cs` |
| gm-items | `.additem` (id, `[name]`, shift-link; negative count removes; partial store; self grants unbound), `.deleteitem` | `Gm/Items/ItemCommands.cs` |
| gm-teleport (part) | `.tele name`, `.recall`, `.goname`, `.namego`; every command teleport stops a taxi flight or saves the recall position | `Gm/Teleport/*`, `Teleport/TeleportCommands.cs` |
| gm-lookup (part) | `.lookup item\|creature\|object\|tele` | `Gm/Lookup/LookupCommands.cs` |
| gm-server-control (part) | `.server shutdown\|restart\|idleshutdown\|idlerestart [cancel \| #delay [#exitcode]]` with the retail countdown schedule and `SMSG_SERVER_MESSAGE`; `.server set motd`; retail `.server info` / `.server motd` | `Gm/Server/*` |
| gm-modify-level (part) | `.modify hp`, `.modify mana`, `.levelup`, `.replenish`, `.deplenish` | `Gm/Character/ModifyCommands.cs` |

Levels (vmangos `AccountTypes`, `Common.h:136-146`): PLAYER 0, MODERATOR 1, TICKETMASTER 2, GAMEMASTER 3,
BASIC_ADMIN 4, DEVELOPER 5, ADMINISTRATOR 6, CONSOLE 7.

| Command | Level | `Chat.cpp` line |
|---|---|---|
| `additem`, `deleteitem`, `levelup`, `replenish`, `deplenish`, `modify hp\|mana` | 3 | 1277-1279, 1274, 1194-1195, 582-583 |
| `lookup` / `lookup item\|creature\|object\|tele` | 1 / 2 | 1205, 557-575 |
| `tele name`, `goname`, `namego` | 2 | 1006, 1236-1237 |
| `recall` | 1 | 1259 |
| `server shutdown\|restart\|idleshutdown\|idlerestart\|set motd` | 6 | 944-998 |
| pre-existing: `saveall` 6, `announce` 4, `notify` 4, `gm` 2, `gm chat` 1, `kick` 2, `tele` 2, `go xyz` 2, `modify money` 4, `learn` 5, `unlearn` 3, `cast` 5, `unaura` 3, `cooldown` 3, `instance listbinds\|unbind` 3, `instance stats` 4, `guild create\|invite\|uninvite\|rank` 3, `guild delete` 4 | | table in `Gm/Core/RetailCommandLevels.cs` with a line per entry |

## Security scale (the one structural decision)

ArcaneCore stores four account levels (`AccountSecurity`: Player, Moderator, GameMaster, Administrator);
vmangos has eight. This lane changes no stored value. `World:GmCommands:SecurityMap` maps each stored level onto
a retail level (default Player 0, Moderator 1, GameMaster 3, Administrator 6), and each command carries a
retail level. A command declared only with an `AccountSecurity` needs the retail level that security maps to,
so commands other lanes wrote keep their meaning.

Consequence to know: retail levels 2, 4 and 5 are reachable only by remapping. With the default map a
GameMaster (3) cannot use `.announce`, `.modify money`, `.learn` or `.cast`; an Administrator (6) can use
everything; a Moderator (1) can `.gm chat` and `.recall` but not `.gm` or `.kick`. Widening `AccountSecurity` to
0-7 is the more faithful fix but renumbers stored values and needs a lead-owned data step (open question below).

## Configuration (`World:GmCommands`)

| Key | Default | Meaning |
|---|---|---|
| `SecurityMap:<AccountSecurity>` | Player 0, Moderator 1, GameMaster 3, Administrator 6 | retail level of a stored level |
| `HideUnavailable` | false | true: a command above the caller behaves as unknown (pre-retail behaviour) |
| `ExactNameFirst` | false | true: an exact name beats a longer name it prefixes, registration order (pre-retail) |
| `RetailLevels` | true | false: keep the four-level declarations of the pre-existing commands |
| `LogCommands` | true | one `ArcaneCore.Gm` log line per command above level 0 |
| `LowerSecurity` | **true** | see below |
| `LookupMaxResults` | 0 (unlimited) | cap `.lookup` output (a one-letter item search matches about 14,000 rows on classic-db) |

`LowerSecurity` is the one deliberate exception to "defaults are retail": vmangos ships `GM.LowerSecurity = 0`
(`mangosd.conf.dist.in:2536`), which lets staff act on a higher account (`.kick`, `.modify`, `.tele name`).
ArcaneCore already refused that, and weakening it would conflict with the standing security rule, so the default
stays strict. Strong checks (mute/unmute) are strict in both. Set `false` for the vmangos behaviour.

## Shared-file edits (for the integrator)

* `Commands/CommandTable.cs`, `Commands/ICommandGroup.cs` rewritten: `ChatCommand` gained trailing optional
  `RetailLevel`; `CommandTable` gained `Lookup`, `ShowHelpForCommand`, `ShowHelpForSubCommands`, `Find`, `IsAvailable`,
  `Gm`; the static `ListNames` was removed; `ChatCommands.CreateTable()` gained overloads and `Build`.
* `Commands/BuiltinCommands.cs`: retail handlers and texts. `help`, `commands`, `save`, `gm`, `kick`, `announce`,
  `notify`, `modify money`, `server info|motd` changed.
* `WorldServiceCollectionExtensions.cs`: one line, `CreateTable(configuration)`. `appsettings.json`: a `World:GmCommands`
  section spelling out the defaults (a test keeps it equal to `GmOptions`).
* `Teleport/TeleportCommands.cs`: `GoHelper` takes the subject player and saves the recall position / stops a flight.
* `ItemTemplateStore.All`, `CreatureContent.Templates`, `GameObjectContent.Templates`: read-only enumerators
  (the hot-reload lane rewrites these stores; the additions are one property each).
* Tests rewritten for retail texts/levels: `M6LogoutAndCommandTests`, `SeamTests` (command order), `TeleportTests`
  (accounts GameMaster instead of Moderator), `SpellWorldTests` (`.learn` by an Administrator). Other lanes' tests that
  run `.learn`/`.cast`/`.announce`/`.modify money`/`.tele`/`.kick` with Moderator or GameMaster accounts will need an
  Administrator account (or `World:GmCommands:RetailLevels=false`), and tests asserting "There is no such command." for
  a staff command now see "This command is not available to you." (or set `HideUnavailable=true`).

No schema version is consumed; nothing was added to Auth, Characters or World.

## Behaviour notes and deviations from vmangos

* Target rank: every GM command that changes another player applies `CommandContext.CanActOn` (the vmangos
  `HasLowerSecurity` rule, `Chat.cpp:1521-1563`) before touching it, so with `LowerSecurity` on a GameMaster cannot
  change an Administrator's character. vmangos calls `HasLowerSecurity` in only some handlers (`.kick`, `.modify *`,
  `.tele name`, `.namego`, `.recall`, `.repairitems`, `.npc whisper`, ...); ArcaneCore applies it to all of them,
  including those vmangos leaves open: `.additem`, `.deleteitem`, `.levelup`, `.replenish`, `.deplenish`, `.revive`,
  `.explorecheat`, `.showarea`, `.hidearea` and `.guild create|invite|uninvite|rank|delete`. `.guild delete` removes
  every member, so it checks each one and refuses if any member outranks the caller. It is SEC_BASIC_ADMIN
  (`Chat.cpp:449`), so under the shipped levels only an Administrator reaches it and nobody outranks an Administrator.
  The check matters once `RetailLevels` is off or `SecurityMap` is remapped. For an offline character
  (`.character rename`, `.guild ...` by name, an offline member of a guild being deleted) the owner account's security
  decides. The guild commands read it off the world thread, so their answer arrives a moment later unless the caller
  cannot be outranked. Commands that only read a
  player (`.gps`, `.honor show`, `.character reputation`, `.instance listbinds`) or move the caller (`.goname`,
  `.gocorpse`) are not gated, as in vmangos. `tests/ArcaneCore.World.Tests/Gm/Core/GmTargetRankTests.cs` sweeps them.
* Online players only. vmangos resolves offline names through the characters database in `.levelup`, `.deleteitem`,
  `.tele name`, `.goname`, `.namego`; here an offline name answers "Player not found!".
* Quest-settlement guard: `.additem`, `.deleteitem` and `.modify money` refuse a player whose quest reward is settling
  (ArcaneCore invariant; the message is not a vmangos text).
* `.additem <id> -N` follows vmangos: removal ignores the bank although the stock check counts it
  (`CharacterCommands.cpp:3350`), and the "not enough" text is the English `.deleteitem` one (vmangos' own is French).
* `.deleteitem`/`.additem` removal respects `CanUnequipItem` (a worn item cannot be removed in combat); vmangos skips it.
* `.additem [name]` takes the lowest entry among duplicate names (classic-db has 280 duplicated names covering 790 items;
  MySQL answers in primary-key order).
* `.levelup` is capped at `Progression:MaxPlayerLevel` (default 60), not vmangos' 255, because the level-stat table ends
  there; talents are not recomputed (no talents area); a selected creature is not levelled (answers a clear message);
  the target is told unless it is the caller (vmangos also stays quiet for GMs invisible to it).
* `.modify hp|mana` work on players only. `.modify mana` writes power index 0 whatever the power type, as vmangos does.
* `.modify energy #value [#max]` and `.modify rage #value [#max]` work on the selected online player or the caller when
  there is no selection. Energy uses displayed whole units; rage uses the classic ten-times wire value and displays
  whole units in the response. An omitted maximum retains at least the current displayed maximum; an explicit maximum
  below the requested value, an overflow, malformed optional argument, or an extra argument is rejected. The current
  power is clamped after the maximum is written and the online player is queued through `World.SavePlayer`; the maximum
  is runtime state only because this branch has no persisted power-cap schema. Responses use mangos LANG ids 122/123
  (ENERGY) and 125/126 (rage), with invariant numeric formatting. A selected creature or offline/missing player returns
  `No character selected.` and security checks run before mutation.
* `.goname` does not create the GM's instance bind and has no battleground rule; the teleport service's instance rules
  apply and a refused teleport answers the invalid-coordinates text (vmangos is silent).
* `.server info` prints "Core revision: ArcaneCore ...", the session count from the session registry (players in the world
  when none is registered), a maximum sampled whenever a player enters the world, and 0 queued (no login queue).
* `.server shutdown` exit codes: the process exit code is set to 0 (shutdown) or 2 (restart) before the host is stopped.
  This relies on the runtime returning `Environment.ExitCode` after `host.RunAsync()`; no test starts the daemon process,
  so that last step is **unverified** (a supervisor must restart on code 2).
* `.help` on a command above the caller still shows its help (vmangos `FindCommand` does not filter by level);
  `.server nonsense` prints "Command  have subcommands:" with an empty name, exactly as vmangos does.
* `.lookup tele` lists lines ordered by id; `.lookup` results are ordered by entry (vmangos walks an unordered map).
* Failed syntax prints the command's help text (or "Incorrect syntax.") followed by its sub-commands, as
  `Chat.cpp:1941-1950`. Existing help strings were rewritten as `Syntax: ...` plus a description line.

## Not delivered (limits)

Skipped because the primitive belongs to another lane or is absent, or because it needs data this lane does not have:

* `.die`, `.revive`, `.neargrave`, `.gocorpse` (death-persistence lane), `.setskill`, `.maxskill` (skills lane), `.reload`
  (hot-reload lane), `.pet` (pets lane).
* gm-info: `.gps` stays the short ArcaneCore line (the full report needs zone-relative coordinates and map/vmap probes),
  `.guid`, `.distance`, `.angle`, `.pinfo` not provided.
* gm-teleport: `.tele group|add|del`, `.groupgo`, `.go` creature/object/grid/graveyard/forward/up/relative/target,
  `.start`, `.unstuck` (need group, spawn-index, graveyard and spell primitives). `.go xyz` keeps its cmangos optional-Z form.
* gm-lookup: `.lookup spell|itemset|quest|area|faction|skill|taxinode|event|pool|player`, `.list`.
* gm-spells (`.aura`, `.nameaura`, retail `.cast` subcommands and untriggered casts, `.learn all`, `.cooldown list|clear`),
  gm-npc (`.npc`, `.respawn`; the non-retail `.creature` root is **kept**, so creature tests and tools still work),
  gm-gobject, gm-spawn-persistence, gm-quest, gm-combat-cheats (`.damage`, `.cheat`): not started; several need the spell,
  creature-AI and quest lanes' primitives.
* gm-items: `.additemset` (needs `ItemTemplate.ItemSet`), `.itemmove`, `.repairitems`.
* gm-modify-speed (needs speed-change packets and acks), `.modify scale|faction|speed|...`, `.character level`,
  `.reset`.
* gm-server-control: `.server plimit`, `corpses`, `resetallraids`, `log`, `exit`.
* gm-moderation (bans, mutes), gm-tickets, gm-command-table-data: each needs a schema module (Auth / Characters /
  World); left for a lane that can coordinate the version numbers.
* GM state is not persisted across logins (retail `GM.LoginState = 2`), and `GmLevelInWhoList` still defaults to
  Administrator where vmangos ships 3 (a one-line edit for the integrator).

## Client checks still pending

1. Shift-click an item link into `.additem` and `.lookup item` result links (`|Hitem:...`), `.lookup tele` links.
2. `.server shutdown 600` shows the countdown banner and `.server shutdown cancel` clears it.
3. `.gm on` shows the notification text; `.gm chat on` shows the badge.
4. `.namego`/`.goname` near and far teleports end to end (the packet flow is the existing teleport service's).
5. A real daemon run returns exit code 2 after `.server restart 5`.

## Open questions

1. Security scale: keep the configurable map over four stored levels, or widen `AccountSecurity` to vmangos' 0-7?
2. `GM.LowerSecurity` default: strict (current, security rule) or vmangos' false?
3. The retail-level flip changes who may run existing commands and several reply texts; run every lane's command tests
   after merging, or land `RetailLevels`/`HideUnavailable` defaults after the audit.
4. Reply texts are taken from mangos-classic at vmangos ids because vmangos' base string table is not in the tree.
5. Speed-change primitive owner (speed auras vs `.modify speed`).

## Reference citations

`D:\refs\vmangos\src\game\Chat\Chat.cpp` 65-1366 (tables), 1488-1563 (isAvailable, HasLowerSecurity), 1566-1600
(hasStringAbbr), 1767-1860 (FindCommand), 1873-1970 (ExecuteCommand), 2081-2163 (help), 2690-3290 (Extract*), 3364,
3717-3800; `Chat.h:134`; `src\shared\Common.h:136-146`; `src\shared\Util.cpp:197-282`;
`Commands\CharacterCommands.cpp` 695-762, 1241-1259, 1849-1872, 3267-3450, 4460-4523; `Commands\UnitCommands.cpp`
2283-2403; `Commands\TeleportCommands.cpp` 28-46, 657-760, 1039-1346; `Commands\LookupCommands.cpp` 167-258, 604-667,
790-850, 1264-1304; `Commands\ServerCommands.cpp` 46-69, 302-323, 370-495; `Commands\MiscCommands.cpp` 37-58, 103-151;
`Commands\AccountCommands.cpp` 489-514; `src\game\World.cpp` 2667-2780; `World.h` 64-81; `Objects\Player.cpp`
2602-2688, 5999-6006, 14881; `Language.h` ids cited in `GmStrings.cs`; `D:\refs\mangos-classic\sql\base\mangos.sql`
3423-3880, 4083-4129; `D:\refs\wow_messages\...\smsg_server_message.wowm`.
