# Logon auto-patcher (`Auth:AutoPatch`)

Off by default. When it's on, a client whose build isn't 5875 gets an MPQ patch over the logon connection instead of "version invalid".

References: vmangos realmd `AuthSocket.cpp` (lines 596-662 for the offer, 1152-1338 for the transfer) and `AuthPackets.h` (`XFER_INIT`, `XFER_DATA_CHUNK`); AscEmu `logonserver/Auth/AutoPatcher.cpp`.

```json
"Auth": {
  "AutoPatch": {
    "Enabled": true,
    "Directory": "patches",
    "FileNamePattern": "{build}{locale}.mpq",
    "Patches": [
      { "Build": 5464, "Locale": "enUS", "File": "5464-to-5875-enUS.mpq" },
      { "Build": 5302, "Locale": "",     "File": "5302-any.mpq" }
    ]
  }
}
```

- **Lookup order:** an exact `Patches` entry for the build and locale, then the same build with an empty (any) locale, then `FileNamePattern` (vmangos `%d%s.mpq`). An empty pattern serves only listed patches.
- **Paths:** relative to `Directory`. A name that resolves outside the folder is ignored, and so is an empty or missing file.
- **Locale:** the client's locale must be one of the ten that realmd accepts.
- **Live reload:** the options are read through `IOptionsMonitor` on each challenge, so editing `appsettings.json` (the default host reloads on change) takes effect from the next connection. File MD5s are cached by path, size and modification time, so replacing a file is also picked up.
- **Flow:** the wrong build completes the challenge, so the account must exist or `AutocreateAccounts` must be on, as in vmangos. Its proof is answered with `[0x01, 0x0A]` (WOW_FAIL_VERSION_UPDATE) plus `XFER_INITIATE {0x30, 5, "Patch", u64 size, md5}`. Nothing is authenticated. The connection then accepts only `XFER_ACCEPT` (0x32), `XFER_RESUME` (0x33 plus u64 offset) or `XFER_CANCEL` (0x34). Data goes out in `{0x31, u16 size, data}` chunks of 4096 bytes.
- **Time limits:** the pre-proof lifetime is disarmed once a patch is offered. `Auth:MaxSessionDurationSeconds` still caps the connection; the client resumes a cut transfer from its `.partial` file.
