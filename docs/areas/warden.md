# Warden (build 5875)

Off by default (`Warden:Enabled`). Code: `src/ArcaneCore.World/Warden/`.

Ported from MaNGOS Zero `src/game/Warden/` (module delivery, handshake, RC4 keys, module initialization, the evidence classes and
confirmation rescans) and vmangos `src/game/Anticheat/WardenAnticheat/` (the scan formats including API hooks, the login-scan flag, the
scripted SYSTEM_INFO and EndScene scans, and the penalty).

## Flow

1. After AUTH_OK, a non-exempt build 5875 session derives both RC4 directions from its 40-byte session key, using the classic SHA-1 generator. It then sends MODULE_USE (module MD5, module key, size).
2. If the client lacks the module, it gets the embedded module (`WardenModuleWin5875.b64`, 18 756 bytes, MD5 `79C0768D...`) once, in 500-byte MODULE_CACHE frames. The module is checked against its pinned MD5 and SHA-256 at load.
3. HASH_REQUEST sends the seed. The client must answer with the expected 20 bytes. Both directions then switch to the module's keys, and MODULE_INITIALIZE installs the client callbacks.
4. A CHEAT_CHECKS_REQUEST goes out every 30-60 s (the first one at a random point of its gap, so sessions do not scan on the same tick) with up to `ScansPerRequest` scans, taken round-robin from `Warden:Checks`, starting at a random check per session, so the end of a long list also runs in short sessions. Each reply is checked against its folded SHA-1 checksum.

Any awaited reply that is later than `ResponseTimeoutSeconds` is a protocol failure. Received bodies are decrypted even when unsolicited so the RC4 stream stays aligned. Under `ProtocolAction=Log`, a protocol failure is logged, nothing is sent back, and the session keeps checking: a failed scan reply or timeout sends the same scan batch once more, and a failure of that retry kicks (so a client cannot skip a chosen check by corrupting its replies), an unsolicited reply keeps the existing scan deadline, and a handshake stage keeps waiting for its one outstanding request (HASH_REQUEST is never re-sent under the old keys). The module is transferred at most once. A failure to answer a request the server made (a bad, malformed or late reply, a failed module load, a module still missing after the transfer) is a strike; the `MaxProtocolFailures`th strike (default 3) kicks the session even under Log, and each valid reply forgives one strike. Unsolicited packets are never strikes. Under Log a session reports at most one protocol failure per `ProtocolReportIntervalSeconds` (default 10), then one line with the count of the rest; a count still held when the session closes or the server stops is reported then. vmangos `src/game/Anticheat/WardenAnticheat/Warden.cpp`, `Warden::HandlePacket`, decrypts before dispatch and kicks on an unexpected opcode, a second module miss or a bad scan checksum (lines 517-532, 546-552, 575-595, 654-665).

## Actions

- `Action` (Log, Kick, Ban): applied when a scan fails. A check can override it with its own `Action`.
- `ProtocolAction`: applied when the handshake fails, a reply is malformed or late, or the module fails to load. Log records the failure and keeps the session, up to `MaxProtocolFailures`; Kick disconnects. Ban acts as Kick: a protocol failure (such as one late reply from a lagging client) never bans, as in vmangos.
- `BanSeconds`: the ban length, 0 for permanent. The ban author is `Warden`.
- `StaffNoticeCooldownSeconds` (default 30, 0 off): a Kick or Ban sends staff online one direct line (account and reason), at most once per account and action per window; repeats are counted and reported as one `N more suppressed in the last 30 s` line. All verdicts of one scan reply (or one packet, tick or flush) are acted on once, at the strongest action: one staff line and one ban however many scans failed. Each verdict is still logged.

## Checks

Scans come from two places:

- **The world table `warden_checks`.** This is world schema version 49, in vmangos's `warden_scans` layout.
  - It is seeded on an empty table with the 94 vmangos rows that apply to build 5875: 80 memory, 6 MPQ, 4 page A and 4 module-by-name.
  - vmangos is GPL-2.0-or-later, so the rows can ship under ArcaneCore's GPL-3.0.
  - Rows whose build range excludes 5875 are skipped, and so are rows that don't parse. API-hook rows (type 6) run; vmangos ships none for this build.
  - Flag 0x08 (vmangos `ScanFlags::InitialLogin`) makes a row a login scan (see below). The other flags need no handling.
  - `penalty` -1 uses `Action`; 0, 1 and 2 mean Log, Kick and Ban.
  - `Warden:LoadFromDatabase` (default true) turns this source off.
- **`Warden:Checks` in config.** These are added to the table's scans, and a configured `Id` replaces the table row with that id.

```json
"Warden": {
  "Enabled": true,
  "Action": "Kick",
  "Checks": [
    { "Id": 1001, "Kind": "Memory", "Address": 4198400, "Expected": "558BEC", "Comment": "main image" },
    { "Id": 1002, "Kind": "PageA", "Address": 0, "Pattern": "DEADBEEF", "Wanted": false },
    { "Id": 1003, "Kind": "Driver", "DriverName": "evil", "DriverPath": "\\Device\\Evil", "Wanted": false, "Action": "Ban" },
    { "Id": 1004, "Kind": "ModuleByName", "Module": "tamia.dll", "Wanted": false },
    { "Id": 1005, "Kind": "Mpq", "Path": "World\\Generic\\x.m2", "Expected": "<40 hex chars, or empty: must be absent>" },
    { "Id": 1006, "Kind": "Lua", "Path": "SomeGlobal", "Expected": "", "Wanted": false },
    { "Id": 1007, "Kind": "ApiHook", "Module": "kernel32.dll", "Function": "GetTickCount", "Expected": "<40 hex chars>", "Address": 0, "Length": 5 },
    { "Id": 1008, "Kind": "Memory", "Address": 4198400, "Expected": "558BEC", "InitialLogin": true }
  ]
}
```

Fields by kind:

- **Memory:** `Module` is optional; empty means the main image. `Expected` is 1-255 hex bytes.
- **PageA / PageB:** `Pattern` is 1-255 hex bytes, sent as HMAC-SHA1 with a random seed.
- **Driver:** a name and a path. The path is sent as HMAC-SHA1.
- **ModuleByName:** the DLL name, upper-cased and sent as HMAC-SHA1.
- **Mpq:** a file path and the expected SHA-1. An empty `Expected` means the file must not exist.
- **Lua:** a global variable name and the value a clean client reports. An empty `Expected` means only existence is checked, using `Wanted`.
- **ApiHook:** vmangos `WindowsHookScan` (opcode 0x7E). `Module` is the DLL, `Function` the export. `Expected` is the 20-byte SHA-1 the
  client compares with `Length` bytes (0-255) at `Address` past the detour's jump target. A 0x4A reply (detoured) fails the scan.
- **InitialLogin** (any kind): the scan runs once, right after MODULE_INITIALIZE, and never in the rotation (vmangos
  `Warden::HandlePacket` sends `SelectScans(InitialLogin)` after the handshake). Every login scan is sent, in back-to-back requests of up
  to `ScansPerRequest`; vmangos sends a random `NumScans` of them.

For page, driver and module checks, `Wanted` says whether the pattern, driver or module should be present.

## Scripted scans

`ScriptedScans` (default false) adds the vmangos scripted scans (`WardenWin::LoadScriptedScans`, code in `WardenScans.cs`). Each is a
memory read at a build 5875 address, and its reply chooses the next read, which goes out as soon as the reply is handled:

- **Warden and SYSTEM_INFO locate** (at login): the module pointer, its WardenSysInfo pointer, then the 36-byte Windows SYSTEM_INFO. A
  failed read, an architecture other than x86, or a processor type other than 386, 486 or 586 fails the scan.
- **EndScene locate** (at login): `g_theGxDevicePtr`, its rendering API (OpenGL stops here: its EndScene is not known), then three
  pointers to the Direct3D EndScene. A failed read, a null device or an unknown API fails the scan.
- **EndScene hook check** (in the rotation, once per pass, after EndScene is found): its first 16 bytes. An INT3 or JMP hook is logged.
  For a JMP, 200 bytes at the target are read and logged as a probable bot when the deobfuscated code starts with pushfd, pushad
  (WRobot) or a full read collapses to under 15 bytes. As in vmangos these are log lines, not failed scans.

Scripted scans have check id 0 and use `Action`.

## Evidence and confirmation rescans

`ConfirmFailedScans` (default false) applies the MaNGOS Zero evidence policy (`WardenEvidence.cpp`, `WardenEnforcementPolicy.cpp`).
Each kind has an evidence class: timing is protocol health, memory and MPQ are integrity invariants, Lua and scripted scans corroborate,
and page, driver, module and API-hook scans are threat signatures.

- A failed scan of any kind but timing, scripted and id 0 is sent again alone, at once (an isolated confirmation rescan), and nothing is
  reported yet.
- The rescan decides. A match clears it (one log line). A confirmed mismatch of an integrity or threat-signature scan takes the scan's
  action. A confirmed unreadable result, or a confirmed corroboration mismatch, is logged once, and the same check and outcome are not
  rescanned again in that session.
- A failed timing or scripted scan cannot be confirmed: it is logged (timing, corroboration), never acted on.
- A failed check with id 0 (memory, MPQ, page, module, driver or API hook, including an unreadable result) is also never confirmed and never
  acts under `ConfirmFailedScans`: it is logged, so the finding stays visible.

Without the option a failed scan takes its action at once, as before.

## Gaps

- **Not verified against a live 1.12.1 client.** The page, driver and module opcodes (0xB2 / 0xBF / 0x71 / 0xD9) come from TrinityCore's table for this module.
- **No Lua rows ship.** vmangos's 5875 set has none.
- **Re-seeding.** An empty table is seeded again at every start. Delete the rows you don't want instead of emptying the table, or set `LoadFromDatabase` to false.
- **Windows only; Mac clients are documented, not supported.** The 5875 auth session does not carry the platform, so a Mac client cannot
  load the Windows module. It gets `ProtocolAction`, and is kicked after `MaxProtocolFailures` strikes even under Log. vmangos has a
  separate Mac path (`WardenMac`: no module, Maiev string-hash scans only, with its own `ScanFlags::Mac` rows); it is not ported, and every
  scan here, including the scripted and login scans, is a Windows scan.
- **Ban lands after the kick.** A Ban verdict disconnects the client at once and writes the ban row afterwards, off the world thread, so until the row commits (a stalled database can hold the write for a long time) the account can log in again, any number of times; each reconnect that fails a scan starts another ban write. When a write commits, the ban store publishes the status change and `BanEnforcementFeature` kicks any live session of the account, and the world rejects the account from then on. A failed write leaves the account free: it is recorded in the fault ledger (`warden.ban`) and the log, and the kick stands. Shutdown waits up to 5 s for writes in flight; one still pending then is recorded as `warden.ban-stop` (the ban may be lost), and a ban that starts after shutdown began is not waited for.
- **Scripted scans are unverified and off by default.** vmangos runs them on every session; they read fixed build 5875 addresses that have
  not been checked against a live 1.12.1 client here.
- **Not ported from the scripted scans:** vmangos writes the SYSTEM_INFO to a `system_fingerprint_usage` log table and holds
  SMSG_CHAR_ENUM until it has been read; here it is kept on the session only, and the character list is not held back. vmangos's other
  scripted scans (CWorld::enables, anti-AFK, Proxifier, hypervisor drivers, click-to-move, the string-hash scan) are not ported.
- **Evidence classes are fixed per kind** (memory and MPQ integrity, Lua corroboration, page, driver, module and API hook threat
  signature). MaNGOS Zero lets each catalogue row choose its class; ArcaneCore has no per-check override. Confirmation dispositions are
  log lines, not MaNGOS Zero's persisted audit and incident rows.
- **SFileCloseFile callback address disagrees between references.** The client's MPQ archive callbacks are sent in MODULE_INITIALIZE command 3, record 1 (`WardenModuleProfile.ArchiveRvas`, encoded by `WardenCodec`); entry 3, the SFileCloseFile callback RVA that MPQ hash scans use, is 0x248730 (MaNGOS Zero `WardenModuleCatalog.cpp`, krilliac-server-Zero `WardenWin.cpp` Function1[3]). vmangos lists 0x253900 (`WardenWin.hpp` file hash check initialization, `WardenWin.cpp`). It goes to every client at module init. Only the real 5875 client binary can settle it, so it is Nathan's client check (it can ride along with the first live 5875 run).
- **Id 0 is never confirmed and never acts under `ConfirmFailedScans`.** MaNGOS Zero confirms only a non-zero check id (`WardenEvidence.cpp` NeedsConfirmation); a failure without confirmation produces no decision there, and its catalogue rejects id 0 at load. Here an id 0 memory, MPQ, page, module, driver or API-hook failure (a mismatch or an unreadable result) is logged only; a scripted or timing scan keeps its own rule above. `Warden:Checks` allows at most one id 0 check: a second makes validation fail, so Warden stays off (the gateway reports not ready). Give configured checks distinct non-zero ids to get the confirmation.
- **Request size.** Queued (login, scripted, confirmation) requests keep to vmangos's 256-byte reply limit; the rotation does not check it.
