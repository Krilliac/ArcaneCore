# AntiCheat options

47 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `AntiCheat`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Action` | AntiCheatAction | `Log` | None \| Log \| GmAlert \| Rubberband \| Kick | World | start | The highest escalation ever applied: None (score only), Log, GmAlert, Rubberband, Kick. A score past a threshold whose action is above this ceiling gets the ceiling instead. Bound from AntiCheat:Action; default Log. Values: `None`, `Log`, `GmAlert`, `Rubberband`, `Kick`. |
| `Autoban:DecayPerHour` | float | `1` | a number, '.' is the decimal point | World | start | Points forgotten per hour. Default 1. |
| `Autoban:Enabled` | bool | `false` | true \| false | World | start | Ban accounts on repeated kicks. Default false. |
| `Autoban:FirstBanSeconds` | long | `86400` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Seconds of the first ban (0 is permanent). Default 86400 (one day). |
| `Autoban:HistoryDays` | int | `180` | a whole number, -2147483648 to 2147483647 | World | start | Earlier AntiCheat bans older than this many days do not move the account up the ladder. Default 180. |
| `Autoban:KickPoints` | float | `10` | a number, '.' is the decimal point | World | start | Points one kick adds. Default 10. |
| `Autoban:LaterBanSeconds` | long | `0` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Seconds of the third and every later ban (0 is permanent). Default 0 (permanent). |
| `Autoban:SecondBanSeconds` | long | `604800` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Seconds of the second ban (0 is permanent). Default 604800 (seven days). |
| `Autoban:Threshold` | float | `25` | a number, '.' is the decimal point | World | start | Points at which the account is banned. Default 25: the third kick within five hours of the first (the fork's 30 with any decay at all needs a fourth kick). |
| `BaselineGapMs` | int | `3000` | a whole number, -2147483648 to 2147483647 | World | start | Caps the time-skip credit (milliseconds): all the skips the evidence supports in ten seconds together earn at most this much. It also caps the time budget of any single step (a silence buys at most this much movement in one step) and, times four, the extra distance the multi-packet budget grants after a stall (the packets a stall held arrive together). A silence between two movement packets is never a free baseline; the step after it is judged against the time that passed. Default 3000. |
| `BurstPacketsPerSecond` | int | `50` | a whole number, -2147483648 to 2147483647 | World | start | Movement packets per second, by the receive clock AND the client's own clock, that count as a burst. Default 50. |
| `ClientTimeRegressionMs` | int | `500` | a whole number, -2147483648 to 2147483647 | World | start | A client timestamp that goes back by more than this many milliseconds is scored. Default 500. |
| `DecayPerSecond` | float | `2` | a number, '.' is the decimal point | World | start | Score points forgotten per second, so occasional noise never adds up (fork AntiCheat.Score.DecayPerSec). Default 2. |
| `Enabled` | bool | `true` | true \| false | World | start | Run the checks at all. Bound from AntiCheat:Enabled; default true (log only, see `Action`). |
| `ExemptManagedBots` | bool | `true` | true \| false | World | start | The server's own managed playerbot sessions are not checked (they are trusted server code). Bound from AntiCheat:ExemptManagedBots; default true. |
| `ExemptSecurity` | AccountSecurity | `Moderator` | Player \| Moderator \| GameMaster \| Administrator | World | start | Accounts at or above this security level are not checked; Player checks everyone (a GM in GM mode is still exempt). Bound from AntiCheat:ExemptSecurity; default Moderator (every staff account). Values: `Player`, `Moderator`, `GameMaster`, `Administrator`. |
| `FallSuppressYards` | float | `20` | a number, '.' is the decimal point | World | start | A drop of this many yards that ends without MSG_MOVE_FALL_LAND is fall-damage suppression. Default 20. |
| `GmAlertIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | At most one GM alert per offender per this many seconds. Default 10. |
| `LatencySlackMs` | int | `1000` | a whole number, -2147483648 to 2147483647 | World | start | The base of the latency slack in milliseconds: the time budget of a movement step is the client's own elapsed time, capped by the server's receive interval plus this plus twice the ping latency average. Default 1000. |
| `Log:CoalesceMs` | int | `5000` | a whole number, -2147483648 to 2147483647 | World | start | Repeats of one violation type by one character within this many milliseconds are folded into the row already queued (its count goes up) instead of adding rows. Default 5000. |
| `Log:FlushIntervalSeconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | Seconds between two flushes of the queued rows. Default 10. |
| `Log:MaxQueuedRows` | int | `10000` | a whole number, -2147483648 to 2147483647 | World | start | Most rows waiting in memory; beyond it the newest are dropped and counted. Default 10000. |
| `Log:MaxRowsPerFlush` | int | `500` | a whole number, -2147483648 to 2147483647 | World | start | Most rows written by one flush (the rest wait for the next). Default 500. |
| `Log:Persist` | bool | `true` | true \| false | World | start | Write violations to character_anticheat_log. Default true. |
| `MaxLatencySlackMs` | int | `3000` | a whole number, -2147483648 to 2147483647 | World | start | The most the latency slack may grow to, in milliseconds. Default 3000. |
| `MaxTimeSkipMs` | int | `10000` | a whole number, -2147483648 to 2147483647 | World | start | A CMSG_MOVE_TIME_SKIPPED reporting more than this many milliseconds is scored (a client freeze rarely exceeds it). Default 10000. |
| `MaxTimeSkipsPer10Seconds` | int | `10` | a whole number, -2147483648 to 2147483647 | World | start | More CMSG_MOVE_TIME_SKIPPED than this in ten seconds is scored as spam. Default 10. |
| `RubberbandIntervalMs` | int | `2000` | a whole number, -2147483648 to 2147483647 | World | start | At most one rubberband per offender per this many milliseconds. Default 2000. |
| `ScoreGmAlert` | float | `30` | a number, '.' is the decimal point | World | start | Score at which the GM alert applies (fork AntiCheat.Score.Warn). Default 30. |
| `ScoreKick` | float | `120` | a number, '.' is the decimal point | World | start | Score at which the session is disconnected (fork AntiCheat.Score.Kick). Default 120. |
| `ScoreRubberband` | float | `60` | a number, '.' is the decimal point | World | start | Score at which the player is moved back to the last validated position (fork AntiCheat.Score.Rubberband). Default 60. |
| `SpeedChangeGraceMs` | int | `2000` | a whole number, -2147483648 to 2147483647 | World | start | After a speed change is acknowledged the previous (higher) speed still counts for this long, in milliseconds, plus the latency slack, so packets already in flight are never judged by the new speed. Default 2000. |
| `SpeedClock:CooldownMs` | int | `10000` | a whole number, -2147483648 to 2147483647 | World | start | After firing it stays silent for this long (milliseconds). Default 10000. |
| `SpeedClock:Enabled` | bool | `true` | true \| false | World | start | Run it. Default true. |
| `SpeedClock:MaxGapMs` | int | `3000` | a whole number, -2147483648 to 2147483647 | World | start | A pair further apart than this (milliseconds) is ignored (the player stood still or the client froze). Default 3000. |
| `SpeedClock:MinSamples` | int | `12` | a whole number, -2147483648 to 2147483647 | World | start | Usable pairs needed before the window is judged. Default 12. |
| `SpeedClock:SustainWindows` | int | `3` | a whole number, -2147483648 to 2147483647 | World | start | Consecutive hot evaluations before it fires. Default 3. |
| `SpeedClock:TolerancePercent` | int | `30` | a whole number, -2147483648 to 2147483647 | World | start | How much faster than real time, in percent, the client clock may run. Default 30. |
| `SpeedClock:Window` | int | `20` | a whole number, -2147483648 to 2147483647 | World | start | Pairs of consecutive packets in the window. Default 20. |
| `SpeedSlackYards` | float | `2` | a number, '.' is the decimal point | World | start | Distance in yards that one packet may always cover on top of the speed budget (rounding, collision push). Default 2. |
| `SpeedTolerancePercent` | float | `10` | a number, '.' is the decimal point | World | start | Tolerance above the allowed speed, in percent, before the speed check scores. Default 10. |
| `SpellHoneypot:Enabled` | bool | `false` | true \| false | World | start | Record the signal. Bound from AntiCheat:SpellHoneypot:Enabled; default false (an ordinary refusal only, as retail). |
| `SpellHoneypot:ToolSpellIds` | List<uint> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each value is a whole number, 0 to 4294967295 | World | start | Extra spell ids that count as traps, for example the ids of a GM tool spell that no player has. Bound from AntiCheat:SpellHoneypot:ToolSpellIds; default empty. |
| `SpellHoneypot:Weight` | float | `60` | a number, '.' is the decimal point | World | start | Score weight of one trapped cast (0 to `AntiCheatScores.MaxWeight`). Default 60: one trapped cast reaches the rubberband score. |
| `StaffNoticeCooldownSeconds` | int | `30` | a whole number, -2147483648 to 2147483647 | World | start | The staff line for a kick, an autoban or a failed autoban is sent at most once per account and kind per this many seconds; repeats inside the window are counted and reported as one "N more suppressed" line. Independent of World:Announce. 0 sends every line. 0-3600, default 30. |
| `TeleportDistance` | float | `50` | a number, '.' is the decimal point | World | start | One packet moving farther than this, plus what the speed covers in the latency slack, is a teleport (fork AntiCheat.Teleport.Distance). Default 50. |
| `TerrainChecks` | bool | `true` | true \| false | World | start | Run the checks that need world geometry (swimming out of water, climbing into the air, walking through a wall). Each runs only where the terrain or vmap data for that spot is actually loaded; without data it never scores. Default true. |
