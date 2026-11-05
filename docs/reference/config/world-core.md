# World core options

55 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AccountDataWriteIntervalMs` | int | `1000` | a whole number, -2147483648 to 2147483647 | World | live | ArcaneCore hardening: the shortest time (milliseconds) between two account data or tutorial writes of one account. A change inside it is kept in memory at once and written when the interval ends, coalesced to the latest value per type (never dropped). 0 = write every change at once. vmangos writes every CMSG_UPDATE_ACCOUNT_DATA (MiscHandler.cpp HandleUpdateAccountData). |
| `AllowTwoSideChat` | bool | `false` | true \| false | World | live | Allow whispers and custom emotes across factions, and send Common/Orcish as Universal (vmangos AllowTwoSide.Interaction.Chat, default off). |
| `AllowTwoSideWhoList` | bool | `false` | true \| false | World | live | Show the other faction in /who (vmangos AllowTwoSide.WhoList, default off). |
| `AutosaveIntervalMs` | int | `900000` | a whole number, -2147483648 to 2147483647 | World | live | Periodic save of online characters, in milliseconds (vmangos PlayerSave.Interval default 900000). 0 disables. |
| `BindAddress` | string | `0.0.0.0` | an IPv4 or IPv6 address such as 0.0.0.0, 127.0.0.1 or ::1 (host names are not resolved) | World | restart | Interface to bind the world listener to. |
| `CharactersPerRealm` | int | `10` | a whole number, -2147483648 to 2147483647 | World | live | Characters an account may have on this realm (vmangos CharactersPerRealm: default 10, at most 10). |
| `CommandTimeBudgetMs` | int | `5` | a whole number, -2147483648 to 2147483647 | World | live | Maximum command-drain time per tick in milliseconds; 0 disables the time bound. |
| `GmLevelInWhoList` | AccountSecurity | `Administrator` | Player \| Moderator \| GameMaster \| Administrator | World | live | Highest staff level ordinary players see in /who (vmangos/cmangos GM.InWhoList.Level default SEC_ADMINISTRATOR, i.e. every account). Values: `Player`, `Moderator`, `GameMaster`, `Administrator`. |
| `InstantLogoutSecurity` | AccountSecurity | `Moderator` | Player \| Moderator \| GameMaster \| Administrator | World | live | Lowest security level that logs out instantly (vmangos/cmangos InstantLogout default SEC_MODERATOR). Values: `Player`, `Moderator`, `GameMaster`, `Administrator`. |
| `ListenRangeSay` | float | `25` | a number, '.' is the decimal point | World | live | Distance /say reaches. vmangos and cmangos-classic World.cpp default to 25 (cmangos' shipped mangosd.conf keeps 25; vmangos' raises it to 40). 0 = the whole map. |
| `ListenRangeTextEmote` | float | `25` | a number, '.' is the decimal point | World | live | Distance /emote and text emotes reach (vmangos/cmangos ListenRange.TextEmote default 25). 0 = the whole map. |
| `ListenRangeYell` | float | `300` | a number, '.' is the decimal point | World | live | Distance /yell reaches (vmangos/cmangos ListenRange.Yell default 300). 0 = the whole map. |
| `LogoutDelayMs` | uint | `20000` | a whole number, 0 to 4294967295 | World | live | How long a requested logout counts down before the player leaves the world (vmangos WorldSession::ShouldLogOut hard-codes 20 seconds; configurable here for tests and tuning). |
| `MaxCommandsPerTick` | int | `1024` | a whole number, -2147483648 to 2147483647 | World | live | Maximum queued world commands admitted before the map pass of one tick. |
| `MaxConnections` | int | `0` | a whole number, -2147483648 to 2147483647 | World | restart | Global cap on simultaneous world connections; 0 = unlimited (retail, the default). Hardening (no vmangos equivalent). |
| `MaxConnectionsPerIp` | int | `0` | a whole number, -2147483648 to 2147483647 | World | restart | Cap per client IP address; 0 = unlimited (retail, the default). Hardening: a retail client holds one connection. |
| `MaxConsecutiveUpdaterFaults` | int | `0` | a whole number, -2147483648 to 2147483647 | World | live | A map updater that throws in this many consecutive ticks is skipped from then on, until `Map.ClearUpdaterFaults` (a code hot reload calls it after each applied edit). 0 (the default) never skips: a throwing updater is logged every tick and the others still run, as before. Only `World:HotCode:Enabled` sets it (docs/areas/code-hot-reload.md). |
| `MaxOutboundBytes` | long | `8388608` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Disconnect a client whose unsent data exceeds this many bytes (it stopped reading). |
| `MaxQueuedWorldBytes` | long | `8388608` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Most payload bytes one session may have queued for the world thread (see `MaxQueuedWorldPackets`); 0 disables the bound. Mirrors the 8 MiB outbound bound. The largest client frame is `WorldSession.MaxClientPacketSize` bytes, so this holds a few hundred maximum-size packets. Bound from World:MaxQueuedWorldBytes. |
| `MaxQueuedWorldPackets` | int | `8192` | a whole number, -2147483648 to 2147483647 | World | start | Most world-handler packets one session may have queued for the world thread; the session is disconnected when a packet would exceed it. 0 disables the bound. The drain rate is `MaxWorldPacketsPerTick` per 50 ms tick (3000 per second) and a retail client sends a few tens of packets per second (movement heartbeats, casts, chat), so 8192 only fills if the world thread stalled for minutes or the peer floods. Hardening: vmangos (WorldSession.cpp:307-332 `QueuePacket` into `m_recvQueue`) and mangos-classic (WorldSession.cpp:256-285) queue without any bound. Bound from World:MaxQueuedWorldPackets. |
| `MaxWorldPacketsPerTick` | int | `150` | a whole number, -2147483648 to 2147483647 | World | start | In-world packets handled per session per tick; the rest wait for the next tick. |
| `Motd` | string | `Welcome to ArcaneCore.` | text | World | live | Message of the day sent as system lines at login; '@' separates lines (vmangos Motd). |
| `PlayerCommands` | bool | `true` | true \| false | World | live | Whether accounts without a GM level may use player commands such as .help and .save (vmangos PlayerCommands, default on). |
| `Port` | int | `8085` | a whole number, -2147483648 to 2147483647 | World | restart | World TCP port. Vanilla default is 8085. |
| `PreAuthTimeout` | TimeSpan | `00:00:10` | a time span, [d.]hh:mm:ss | World | start | How long a world connection may stay unauthenticated (no valid CMSG_AUTH_SESSION yet), counted from accept; the session is closed when it expires. This is the retail rule: vmangos `Network.TimeoutSecsIfNoAuth = 10` (WorldSocket.cpp:621-628, mangosd.conf.dist.in:3039) closes the socket the same way. It stops a client that withholds the packet header (or trickles it) from holding a socket, a writer task and a DI scope forever (Codex finding 2). `TimeSpan.Zero` disables it. Bound from World:PreAuthTimeout (for example "00:00:10"). |
| `SaveQueueWarnAgeMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | World | live | The character save queue warns while its oldest waiting snapshot has waited this many milliseconds. 0 disables the age check. Default 5000, the characters database's write bound (Database:SqliteWriteTimeoutMs): a snapshot that old has waited out at least one write-gate bound (5 s without a hand-off; a moving queue may keep a waiter up to 20 s) (docs/areas/persistence.md). |
| `SaveQueueWarnDepth` | int | `1000` | a whole number, -2147483648 to 2147483647 | World | live | The character save queue warns (at most every 30 s) while this many characters or more have a snapshot waiting or being written. 0 disables the depth check. Default 1000 (docs/areas/persistence.md). |
| `StrictMovementFiniteness` | bool | `false` | true \| false | World | start | Hardening beyond retail: also require pitch, jump speeds/angles and spline elevation to be finite before a movement block is stored and relayed. Retail (vmangos VerifyMovementInfo) does not check them, so the default is off. Bound from World:StrictMovementFiniteness. |
| `TickIntervalMs` | int | `50` | a whole number, -2147483648 to 2147483647 | World | restart | World tick length in milliseconds (vmangos WORLD_SLEEP_CONST = 50). |
| `TickLateToleranceMs` | int | `2` | a whole number from 0 to 1000 | World | restart | Milliseconds a tick may start after its due time, and a frame may exceed the tick interval, before it counts as late (`WorldTickScheduler.LateTicks`) or as a frame overrun (0..1000, default 2). Read once at world start. |
| `TickTimer` | WorldTickTimer | `Precise` | Precise \| Legacy | World | restart | How the world thread waits for its next tick (`WorldTickTimer.Precise` by default). Precise wakes within a fraction of a millisecond of the due time; Legacy is the plain event wait, which on Windows wakes on the ~15.6 ms system timer (frame p50 46 / p90 61 ms on a 50 ms tick; docs/integration/perf-limits-20261008.md). Read once at world start. Values: `Precise`, `Legacy`. |
| `UpdateCompressionThreshold` | int | `128` | a whole number, -2147483648 to 2147483647 | World | live | Update packets larger than this many bytes are zlib-compressed into SMSG_COMPRESSED_UPDATE_OBJECT (vmangos Compression.Update.Size default 128). 0 disables. |
| `WorldCloseGrace` | TimeSpan | `00:00:00` | a time span, [d.]hh:mm:ss | World | start | How long a closing session waits for its close command to run on the world thread, behind the commands it queued earlier (an EnterWorld already past its claim check, a handler mid-run), so they finish while the session's DI scope is still alive. `TimeSpan.Zero`, the default, means the built-in 10 s bound; the wait is never unbounded and a timeout is logged (the scope then ends anyway). It is skipped when no world thread runs. vmangos has no per-connection scope and so no such wait. Bound from World:WorldCloseGrace (for example "00:00:05"). |
| `WriterDrainGrace` | TimeSpan | `00:00:00` | a time span, [d.]hh:mm:ss | World | start | How long a closing session lets the writer flush queued frames (for example a refusal reply) before the stream is torn down so a client that stopped reading cannot hold the connection, its DI scope and its queued frames forever. `TimeSpan.Zero`, the default, means the built-in 5 s bound; the wait is never unbounded. vmangos does not wait at all: its CloseSocket shuts the socket down at once (AsyncSocket_windows.cpp:318-329). Bound from World:WriterDrainGrace (for example "00:00:02"). |

## `World:Console`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `true` | true \| false | World | start | Accept typed GM commands at the daemon's console window when standard input is an interactive console. The commands run on the world thread with Administrator authority and no character. Never starts a reader when input is redirected (services, tests, deploy scripts). Default on. |

## `World:Exploration`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `CorrectExploreCheat` | bool | `false` | true \| false | World | start | `.explorecheat` as vmangos writes it (CharacterCommands.cpp:640-679) is buggy: it announces the SELECTED player but changes the ISSUER's fields, and "0" ORs in 0 (does nothing). Default false (retail/vmangos). True applies the effect to the selected player and really clears on 0. |
| `RateXp` | float | `1` | a number, '.' is the decimal point | World | start | vmangos `Rate.XP.Explore` (default 1). |

## `World:MeetingStone`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `LeaveSoloQueueOnPartyJoin` | bool | `false` | true \| false | World | start | ArcaneCore deviation, off by default (retail): a player queued alone at a meeting stone who joins a party by an ordinary invitation leaves the solo queue (the client is told SETQUEUE LEAVE_QUEUE). vmangos keeps the stale entry (Group.cpp AddMember only updates the LFG state of a party that is itself in the queue), so that player keeps blocking the roles of everyone queued after them until they log out, and is lost from a pair the queue forms. This also applies to the inviter of a new party who had queued alone. |

## `World:Realm`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `HeartbeatIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | Seconds between the heartbeats this world writes into its realm-list row while its loop is ticking (ArcaneCore-specific; vmangos has no liveness check). The logon daemon shows the realm offline once the last beat is older than `Realm:Liveness:HeartbeatStaleSeconds` (default 60, which must be at least three times this interval). A beat is written only if the world loop started a tick since the last one, so a hung or stalled loop stops beating while the process is still up. Needs `World:Realm:Id` above 0. |
| `Id` | int | `1` | a whole number, -2147483648 to 2147483647 | World | start | The realm-list id this world serves (vmangos RealmID; mangosd.conf.dist.in ships 1). The world clears its Offline flag at start and sets it at a clean stop, so the realm list shows the realm offline while the world is down. 0 = this world does not write the realm list (ArcaneCore-specific: vmangos refuses to start without a RealmID, Master.cpp:475-480). |
| `PlayerLimit` | int | `100` | a whole number, -2147483648 to 2147483647 | World | start | The player limit (vmangos PlayerLimit, default 100, SystemConfig.h:91): a plain player is refused at login once this many other accounts are online (staff accounts always get in; `.server plimit` changes it at run time and `.server plimit reset` returns to this value; ArcaneCore has no login queue, so the refusal is AUTH_UNAVAILABLE). The realm list's population hint is 2 x sessions / PlayerLimit, written when a session is added (World.cpp:340-353). 0 = no limit and the population is never written (vmangos skips it when the limit is 0). |

## `World:Time`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `TimeZoneId` | string | `(empty)` | text | World | start | An explicit zone id (IANA or Windows) to treat as "server local time"; empty = the machine's zone. Only read while `UseServerLocalTime` is true. |
| `UseServerLocalTime` | bool | `true` | true \| false | World | start | Pack the SERVER's local time into SMSG_LOGIN_SETTIMESPEED and compute weather seasons and event dates in it, as vmangos does with `localtime` (Server/Packets/Misc.cpp:924-933, Weather.cpp:100-103). Default true (retail); false uses UTC. |

## `World:Transports`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `false` | true \| false | World | start | Master switch. Set it to true once the content is present: the gameobject_template type 15 rows and the transports periods (tools/content/refresh-world-content.ps1 writes both) and a build-5875 TaxiPathNode.dbc through `NpcServices:TaxiPathNodeDbcPath`; the world then logs "Transports: 9 routes, 9 ships sailing". Off (the default), no ship is built or spawned and a client that claims to stand on a transport is treated as standing on nothing. vmangos always runs its ships (World.cpp:1451 LoadTransportTemplates). |
| `Entries` | List<uint> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is a whole number, 0 to 4294967295 | World | start | Only these `gameobject_template` entries become ships; empty (the default) means every type 15 row with a usable path. A switch for bringing routes up one at a time. |

## `World:WarMode`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Enabled` | bool | `false` | true \| false | World | live | Switch War Mode on for every player and bot, with no opt-out. Default false (retail). Switching it off returns the flags to the normal rules: a flag then lapses by its 5-minute timer unless the player wants it. |
| `ExemptAreas` | string | `35,392,976,2255,2361,2268,3425` | text | World | live | Area or zone ids (comma separated) War Mode leaves alone besides the capitals and cities of a player's own team: the neutral towns, which the 1.12.1 area table flags neither Capital nor City (they carry flag 0x40 and no team). The default is 35 Booty Bay, 392 Ratchet, 976 Gadgetzan, 2255 Everlook, 2361 Nighthaven, 2268 Light's Hope Chapel and 3425 Cenarion Hold. Arenas (the Gurubashi Battle Ring, 2177; not the stands around it, 1741) and battlegrounds are always exempt. Live. |
| `HonorMultiplier` | float | `1.5` | 1 to 10 | World | live | While War Mode is on, the honour of an honourable kill is multiplied by this (1 to 10, default 1.5). 1 gives the same honour as with War Mode off. |
| `PvpDeathDurabilityLoss` | float | `0.1` | 0 to 1 | World | live | While War Mode is on, the fraction of every equipped item's durability a player loses when killed by another player outside a battleground (0 to 1, default 0.10). 0 costs nothing. Retail, and War Mode off: a PvP death costs no durability. `Items:DurabilityLossEnable` = false still turns all durability loss off. |

## `World:Weather`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ChangeIntervalMs` | uint | `600000` | a whole number, 0 to 4294967295 | World | start | vmangos `ChangeWeatherInterval`: ms between weather regenerations of a zone (default 10 minutes, World.cpp:596). |
| `Enabled` | bool | `true` | true \| false | World | start | vmangos `ActivateWeather` (default 1, World.cpp:730). |

## `World:WorldStates`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `DefaultsPath` | string | `(empty)` | text | World | start | A JSON file holding the default world states sent with every zone entry, as `[[state, value], ...]` (vmangos hard-codes 108 sniffed pairs, Player.cpp:8041-8213; that table is GPL data and is not shipped here). Empty (default) = no default pairs, like mangos-classic. |

## `World:Zones`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `ClientZoneTrust` | ClientZoneTrust | `Auto` | Auto \| Never \| Always | World | start | See `ClientZoneTrust`. Default `ClientZoneTrust.Auto`: a world with imported area data behaves like retail; one without keeps accepting the client zone. This is a documented development allowance, not retail behaviour. Values: `Auto`, `Never`, `Always`. |
| `PvpRealmMode` | PvpRealmMode | `Normal` | Normal \| Pvp \| FfaPvp | World | start | The realm kind for PvP rules (vmangos `IsPvPRealm` / `IsFFAPvPRealm`): Normal (default), Pvp or FfaPvp. Values: `Normal`, `Pvp`, `FfaPvp`. |
| `RelocationCheckDelayMs` | uint | `0` | a whole number, 0 to 4294967295 | World | start | vmangos `Movement.RelocationVmapsCheckDelay` (default 0, at most 2000): when above 0 the explore check runs this many ms after a position change instead of at once (Player.cpp:5975-5985). |
