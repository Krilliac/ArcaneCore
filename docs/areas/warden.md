# Warden (build 5875)

Off by default (`Warden:Enabled`). Code: `src/ArcaneCore.World/Warden/`.

Ported from MaNGOS Zero `src/game/Warden/` (module delivery, handshake, RC4 keys, module initialization) and vmangos
`src/game/Anticheat/WardenAnticheat/` (the memory, page and driver scan formats and the penalty).

## Flow

1. After AUTH_OK, a non-exempt build 5875 session derives both RC4 directions from its 40-byte session key, using the classic SHA-1 generator. It then sends MODULE_USE (module MD5, module key, size).
2. If the client lacks the module, it gets the embedded module (`WardenModuleWin5875.b64`, 18 756 bytes, MD5 `79C0768D...`) once, in 500-byte MODULE_CACHE frames. The module is checked against its pinned MD5 and SHA-256 at load.
3. HASH_REQUEST sends the seed. The client must answer with the expected 20 bytes. Both directions then switch to the module's keys, and MODULE_INITIALIZE installs the client callbacks.
4. A CHEAT_CHECKS_REQUEST goes out every 30-60 s with up to `ScansPerRequest` scans, taken round-robin from `Warden:Checks`. Each reply is checked against its folded SHA-1 checksum.

Any awaited reply that is later than `ResponseTimeoutSeconds` is a protocol failure.

## Actions

- `Action` (Log, Kick, Ban): applied when a scan fails. A check can override it with its own `Action`.
- `ProtocolAction`: applied when the handshake fails, a reply is malformed or late, or the module fails to load. It is never above Kick.
- `BanSeconds`: the ban length, 0 for permanent. The ban author is `Warden`.

## Checks

Scans come from two places:

- **The world table `warden_checks`.** This is world schema version 49, in vmangos's `warden_scans` layout.
  - It is seeded on an empty table with the 94 vmangos rows that apply to build 5875: 80 memory, 6 MPQ, 4 page A and 4 module-by-name.
  - vmangos is GPL-2.0-or-later, so the rows can ship under ArcaneCore's GPL-3.0.
  - Rows whose build range excludes 5875 are skipped. So are API-hook rows (type 6, not ported) and rows that don't parse.
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
    { "Id": 1006, "Kind": "Lua", "Path": "SomeGlobal", "Expected": "", "Wanted": false }
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

For page, driver and module checks, `Wanted` says whether the pattern, driver or module should be present.

## Gaps

- **Not verified against a live 1.12.1 client.** The page, driver and module opcodes (0xB2 / 0xBF / 0x71 / 0xD9) come from TrinityCore's table for this module.
- **No Lua rows ship.** vmangos's 5875 set has none.
- **Re-seeding.** An empty table is seeded again at every start. Delete the rows you don't want instead of emptying the table, or set `LoadFromDatabase` to false.
- **Windows only.** The 5875 auth session does not carry the platform, so a Mac client cannot load the module and gets `ProtocolAction`.
- **Not ported:** API-hook scans, the vmangos sysinfo and EndScene scripted scans, the `InitialLogin` scan flag, and MaNGOS Zero's evidence classes and confirmation rescans.
