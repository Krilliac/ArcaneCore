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

```json
"Warden": {
  "Enabled": true,
  "Action": "Kick",
  "Checks": [
    { "Id": 1, "Kind": "Memory", "Address": 4198400, "Expected": "558BEC", "Comment": "main image" },
    { "Id": 2, "Kind": "PageA", "Address": 0, "Pattern": "DEADBEEF", "Wanted": false, "Comment": "hack signature" },
    { "Id": 3, "Kind": "Driver", "DriverName": "evil", "DriverPath": "\\Device\\Evil", "Wanted": false, "Action": "Ban" }
  ]
}
```

Fields by kind:

- **Memory:** `Module` is optional; empty means the main image. `Expected` is 1-255 hex bytes.
- **PageA / PageB:** `Pattern` is 1-255 hex bytes, sent as HMAC-SHA1 with a random seed.
- **Driver:** a name and a path. The path is sent as HMAC-SHA1.

For page and driver checks, `Wanted` says whether the pattern or driver should be present. When no checks are configured, only the timing scan runs.

## Gaps

- No scan list ships. Memory addresses and signatures must be configured; vmangos keeps them in the world DB table `warden_scans`.
- Only the Windows module exists. The 5875 auth session does not carry the platform, so a Mac client cannot load the module and gets `ProtocolAction`.
- Not ported: MPQ hash and Lua scans, module-by-name and API-hook scans, the vmangos sysinfo and EndScene scripted scans, and MaNGOS Zero's evidence classes and confirmation rescans.
