# GameEvents options

10 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `World:GameEvents`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `AllowReload` | bool | `false` | true \| false | World | start | Registers a `.reload game_event` sub-command. Retail has none, so the default is false; it still needs `HotReload:Commands` like every reload. |
| `Announce` | bool | `false` | true \| false | World | start | vmangos `Event.Announce` (GameEventMgr.cpp:788-789): tell every player in the world when an event starts. Default false (retail default). |
| `DateTimeInterpretation` | GameEventDateTimeInterpretation | `Wall` | Wall \| StandardTime | World | start | How a `start_time` / `end_time` that has no zone is read. Default `Wall` (vmangos: MySQL `UNIX_TIMESTAMP` reads the session zone, GameEventMgr.cpp:183); `StandardTime` reproduces mangos-classic, whose `std::mktime` on a zeroed `tm` never applies daylight saving (Field.cpp:24-31). Values: `Wall`, `StandardTime`. |
| `Dialect` | GameEventDialect | `Auto` | Auto \| CMangos \| VMangos | World | start | Which `game_event` table layout the data is in (`Events.GameEventDialect`). Default `Auto`: decided from the table's own columns when it is loaded; set it only to override a wrong guess. Values: `Auto`, `CMangos`, `VMangos`. |
| `Enabled` | bool | `true` | true \| false | World | start | Whether the game-event service runs at all. Default true. With false no event ever starts: holiday content, event quests and event spawns stay off (the pre-wave-4 behaviour). |
| `LeapDayMode` | LeapDayMode | `DateStable` | DateStable \| VmangosLiteral | World | start | How yearly events count February 29th (`Events.LeapDayMode`). Default `DateStable` (holidays keep their calendar date, as in retail); `VmangosLiteral` reproduces the vmangos loop, whose yearly events start a day late in many years. Values: `DateStable`, `VmangosLiteral`. |
| `ManualStartLengthUnit` | GameEventManualLengthUnit | `Seconds` | Seconds \| Minutes | World | start | The unit a manual `.event start` adds the event length in when its end has passed (`Events.GameEventManualLengthUnit`). Default `Seconds`: retail (vmangos GameEventMgr.cpp:95, mangos-classic) adds the minutes count as seconds, so such an event is stopped again after about `length` seconds; `Minutes` lets it run its whole length. Values: `Seconds`, `Minutes`. |
| `RestoreServersideEvents` | bool | `false` | true \| false | World | start | mangos-classic never restores a serverside (`schedule_type` 0) event that was active at shutdown (its Update skips them and CheckOneGameEvent is false for their far-future start, GameEventMgr.cpp:634-690), vmangos leaves that state to its hardcoded handlers. Default false (retail); true re-applies the serverside events recorded in `game_event_status` with the resume flag, so their progress is not lost. |
| `StartBoundary` | GameEventStartBoundary | `Auto` | Auto \| Inclusive \| Exclusive | World | start | Whether an event is active AT its start instant (vmangos `start &lt;= current`, GameEventMgr.cpp:41) or only after it (mangos-classic `start &lt; current`, GameEventMgr.cpp:36-40). Default `Auto`: the rule of the table's dialect (the vmangos rule for vmangos tables, the mangos-classic rule for classic-db tables). Values: `Auto`, `Inclusive`, `Exclusive`. |
| `YearlyRebase` | YearlyRebaseMode | `SpanNewYear` | SpanNewYear \| MangosLiteral | World | start | How yearly (`schedule_type` 11) events are moved to the current year (`Events.YearlyRebaseMode`). Default `SpanNewYear` (a holiday that crosses New Year keeps running, as in retail); `MangosLiteral` is the mangos-classic code, which cuts such a holiday at December 31st. Values: `SpanNewYear`, `MangosLiteral`. |
