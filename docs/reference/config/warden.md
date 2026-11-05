# Warden options

17 options. Set them in the daemon's `.conf` file or `appsettings.json`. Full reference: [configuration.md](../configuration.md). All areas: [index](README.md).

Daemon says which server reads it. Reload says whether `.reload config` applies it.


## `Warden`

| Option | Type | Default | Allowed | Daemon | Reload | What it does |
|---|---|---|---|---|---|---|
| `Action` | WardenAction | `Log` | Log \| Kick \| Ban | World | start | Warden:Action, applied when a scan fails (default Log). Values: `Log`, `Kick`, `Ban`. |
| `BanSeconds` | long | `86400` | a whole number, -9223372036854775808 to 9223372036854775807 | World | start | Warden:BanSeconds for `WardenAction.Ban`; 0 is permanent. Default 86400 (vmangos Warden.ClientBanDuration). |
| `Checks` | List<WardenCheckOptions> | `(empty)` | a list: numbered keys 0, 1, 2, ...; each entry is a group of keys | World | start | Warden:Checks: scans added to the table's (a configured id replaces the table row with that id). With neither, the timing scan runs alone. |
| `ChunkSize` | int | `500` | a whole number, -2147483648 to 2147483647 | World | start | Warden:ChunkSize: module bytes per MODULE_CACHE frame (MaNGOS Zero 500). |
| `ConfirmFailedScans` | bool | `false` | true \| false | World | start | Warden:ConfirmFailedScans: the MaNGOS Zero evidence policy (WardenEvidence, WardenEnforcementPolicy). A failed memory, MPQ, Lua, page, driver, module or API-hook scan is first sent again alone (an isolated confirmation rescan), and only a confirmed mismatch of an actionable evidence class (integrity, threat signature) takes the scan's action. A confirmed unreadable result or a corroboration (Lua, scripted) mismatch is logged once, and a failed timing scan is logged. Default false: a failed scan acts at once. |
| `Enabled` | bool | `false` | true \| false | World | start | Warden:Enabled; default false. |
| `ExemptSecurity` | AccountSecurity | `Moderator` | Player \| Moderator \| GameMaster \| Administrator | World | start | Warden:ExemptSecurity: accounts at or above it are never scanned. Default Moderator. Values: `Player`, `Moderator`, `GameMaster`, `Administrator`. |
| `LoadFromDatabase` | bool | `true` | true \| false | World | start | Warden:LoadFromDatabase: also run the scans of the world table `warden_checks` for this build. Default true. |
| `MaxProtocolFailures` | int | `3` | a whole number, -2147483648 to 2147483647 | World | start | Warden:MaxProtocolFailures: protocol failures to the server's own requests (a bad or late reply, a failed module load) that disconnect the session even under ProtocolAction Log. Each valid reply forgives one. Unsolicited packets never count. 1-100, default 3. |
| `ProtocolAction` | WardenAction | `Log` | Log \| Kick \| Ban | World | start | Warden:ProtocolAction, applied when the handshake or a reply breaks the protocol (a wrong hash, a malformed or late reply, a failed module load). Log keeps the session and keeps scanning, up to `MaxProtocolFailures`; Kick disconnects. Ban is treated as Kick, so a late reply from a lagging client never bans an account (vmangos kicks for every protocol failure). Default Log. Values: `Log`, `Kick`, `Ban`. |
| `ProtocolReportIntervalSeconds` | uint | `10` | a whole number, 0 to 4294967295 | World | start | Warden:ProtocolReportIntervalSeconds: under ProtocolAction Log, a session reports at most one protocol failure per interval and then one line with the count of the rest. 1-3600, default 10. |
| `ResponseTimeoutSeconds` | uint | `30` | a whole number, 0 to 4294967295 | World | start | Warden:ResponseTimeoutSeconds: every awaited reply must arrive within this (MaNGOS Zero WardenLimits.deadlineMs 30000). |
| `ScanIntervalMaxSeconds` | uint | `60` | a whole number, 0 to 4294967295 | World | start | Warden:ScanIntervalMaxSeconds: the longest gap between scan requests. Default 60. |
| `ScanIntervalMinSeconds` | uint | `30` | a whole number, 0 to 4294967295 | World | start | Warden:ScanIntervalMinSeconds: the shortest random gap between scan requests (MaNGOS Zero normal interval 30-60 s). |
| `ScansPerRequest` | int | `3` | a whole number, -2147483648 to 2147483647 | World | start | Warden:ScansPerRequest (vmangos Warden.NumScans). Default 3. |
| `ScriptedScans` | bool | `false` | true \| false | World | start | Warden:ScriptedScans: also run the vmangos scripted scans (WardenWin::LoadScriptedScans): at login the Warden module and the client's SYSTEM_INFO are located and checked (x86, an i386/i486/Pentium processor type), and the Direct3D EndScene is located and then checked in the rotation for an INT3 or JMP hook (a hook is logged, as vmangos does). Uses the build 5875 client offsets. Default false (vmangos always runs them; they are not yet verified against a live 1.12.1 client). |
| `StaffNoticeCooldownSeconds` | uint | `30` | a whole number, 0 to 4294967295 | World | start | Warden:StaffNoticeCooldownSeconds: the staff line for a Kick or Ban verdict is sent at most once per account and action per this many seconds; repeats inside the window are counted and reported as one "N more suppressed" line. Independent of World:Announce. 0 sends every line. 0-3600, default 30. |
