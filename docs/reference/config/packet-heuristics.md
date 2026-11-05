# PacketHeuristics options

23 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:PacketHeuristics`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Cadence` | bool | `true` | true \| false | World | start | Report an opcode class sent faster than its ceiling (`cadence`). Default true. |
| `CadenceLimits:CastPerSecond` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | CMSG_CAST_SPELL and CMSG_PET_CAST_SPELL per second. 0-1000, default 10. |
| `CadenceLimits:EmotePerSecond` | int | `5` | a whole number, -2147483648 to 2147483647 | World | start | CMSG_EMOTE and CMSG_TEXT_EMOTE per second. 0-1000, default 5. |
| `CadenceLimits:PingMinIntervalMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | World | start | A CMSG_PING that follows the previous one by less than this many milliseconds is reported (a 1.12.1 client pings every 30 seconds). 0 switches it off. 0-60000, default 5000. |
| `CadenceLimits:QueryPerSecond` | int | `50` | a whole number, -2147483648 to 2147483647 | World | start | Query opcodes (names containing QUERY) per second. A client entering a crowded city can burst; raise it if that is all you see. 0-1000, default 50. |
| `CadenceLimits:UsePerSecond` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | CMSG_USE_ITEM, CMSG_GAMEOBJ_USE and CMSG_LOOT per second. 0-1000, default 10. |
| `DevOpcode` | bool | `true` | true \| false | World | start | Report a stripped developer, GM, cheat or debug opcode that the release client never sends (`dev-opcode`); the packet is dropped unread either way. Default true. |
| `Enabled` | bool | `false` | true \| false | World | start | Run the checks at all. Default false: with it off a packet costs one volatile read and no per-session state exists. |
| `ExemptBots` | bool | `true` | true \| false | World | start | The server's own managed playerbot sessions are not checked. Default true; with false their traffic is checked and counted as bot traffic. |
| `ExemptStaff` | bool | `true` | true \| false | World | start | Staff (Moderator and above, or a player in GM mode) are not checked. Default true; with false their traffic is checked and counted as staff traffic. |
| `IncapacitatedAction` | bool | `true` | true \| false | World | start | Report an action opcode received while the player is dead or a ghost, on a taxi flight or in a far transfer (`incapacitated-action`; a near teleport such as Blink is not one). Default true. |
| `Malformed` | bool | `true` | true \| false | World | start | Report a payload a handler rejected as malformed, or a bad frame header (`malformed`). Default true. |
| `OrphanAck` | bool | `true` | true \| false | World | start | Report an unsolicited or mismatching movement acknowledgement (`orphan-ack`). Default true. |
| `Replay` | bool | `true` | true \| false | World | start | Report the same opcode with the same payload repeated (`replay`). Default true. |
| `ReplayBuiltInExclusions` | bool | `true` | true \| false | World | start | Keep the built-in list of opcodes that legitimately repeat an identical payload out of the replay check: movement and its acknowledgements, ping, every query, selection and attack toggles, cancels, stand state, loot release, area triggers, character enumeration and the polling a client or bot does (docs/areas/packet-heuristics.md has the list and the reason for each entry). Default true; set false to take full control with `ReplayExclude`. |
| `ReplayCount` | int | `5` | a whole number, -2147483648 to 2147483647 | World | start | The same opcode and payload this many times within `ReplayWindowMs` is a replay. 2-1000, default 5. |
| `ReplayExclude` | string[] | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is text | World | start | More opcodes never checked for replay, on top of the built-in list: exact opcode names (CMSG_PING) or names with a leading and/or trailing * (MSG_MOVE_*, *QUERY*). A configured list is added to the default (empty) one, so entries never remove anything; use `ReplayBuiltInExclusions` to drop the built-in list. Default empty. |
| `ReplayWindowMs` | int | `1000` | a whole number, -2147483648 to 2147483647 | World | start | The window of the replay check, in milliseconds. 100-60000, default 1000. |
| `ReportIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | At most one ledger report per session, check and opcode per this many seconds; every occurrence is still counted. 1-3600, default 10. |
| `Shape` | bool | `true` | true \| false | World | start | Report a payload whose length differs from the fixed length of its message (`shape`). Default true. |
| `UnknownOpcode` | bool | `true` | true \| false | World | start | Report an opcode with no handler or outside the opcode table (`unknown-opcode`). Default true. |
| `WrongState` | bool | `true` | true \| false | World | start | Report an opcode dropped because the session state does not allow it (`wrong-state`). Default true. |
| `WrongStateGraceMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | World | start | After the player left the world (logout, back at the character screen) world opcodes are expected for this many milliseconds (packets in flight) and are not reported as `wrong-state`. Only this post-logout window is timed: a map-less player in a far teleport is excused by the teleport stage, however long the loading screen takes. 0-60000, default 5000. |
