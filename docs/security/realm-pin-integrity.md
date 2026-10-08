# Realm PIN, TOTP and client integrity (2026-10-08)

ArcaneCore's build-5875 realm logon now supports the classic PIN grid. The account's
auth-schema-v5 `LockFlags` use the vmangos `LockFlag` bits: `IpLock=1`, `FixedPin=2`,
`Totp=4`, `AlwaysEnforce=8`. `SecurityInfo` holds decimal PIN digits or an unpadded
Base32 TOTP secret. `LastIp` is written with each successful realm proof.

The challenge includes `securityFlags=1`, a random four-byte grid seed and a random
16-byte salt when the address differs from an IP-locked account's last address or
`AlwaysEnforce` is set. A mismatched IP without a factor is refused at challenge.
The proof includes a 16-byte client salt and 20-byte hash when its PIN flag is set;
an expected PIN that is absent or wrong fails before the session key is stored.
Fixed PIN and TOTP are mutually exclusive. TOTP is RFC 6238 SHA-1, six digits,
30-second steps, with vmangos' offsets -2, -1, 0 and +1.

One deliberate difference from vmangos: it turns the stored PIN (`std::stoi`) and
each TOTP code (a `uint32`) into digits by repeated division, which drops leading
zeros, so a PIN such as `0042` and roughly one TOTP code in ten (those below
`100000`) can never match what the player types. ArcaneCore keeps the digits as
entered: a fixed PIN is checked digit for digit and a TOTP code is always six
digits with its leading zeros, as authenticator apps display it.

Behavior reference: vmangos `src/realmd/AuthSocket.cpp` challenge account policy
and IP lock at lines 393-414, PIN grid at 497-517, PINData read at 578-587,
factor check at 689-738, `VerifyPinData` at 1204-1272, and `GenerateTotpPin`
at 1274-1301. The PIN permutation and hash use gtker's WowSrp source, recorded
in [THIRD_PARTY_NOTICES.md](../../THIRD_PARTY_NOTICES.md).

## Manage a factor

`arcane-account set-pin USER` and `arcane-account set-totp USER` read the secret
from a no-echo prompt. Pipe a line and pass `--secret-stdin` in automation.
`clear-pin` and `clear-totp` remove the factor and IP lock together, to avoid
leaving an address lock that has no challenge method. `set-ip-lock USER on|off`
toggles address locking. Setting or clearing a factor revokes the stored session key.

An Administrator can run `.account pin set USER`, `.account totp set USER`, and
their `clear` forms. `set` generates a secret on the server and sends it only in
the command reply. This keeps secret text out of the GM command audit line.
`.account iplock USER on|off` changes the address lock. A TOTP secret given in a
GM reply should be enrolled in an authenticator before disconnecting.

## Client integrity

`Auth:StrictVersionCheck` defaults to `false`, as in vmangos
`AuthSocket::VerifyVersion` (lines 1433-1470). When enabled, configure exact
`Auth:IntegrityHashes` entries with `Build`, `Os`, `Platform` and `Hash` (40 hex
characters). For example, build 5875 with `Win` and `x86` matches a native Windows
client. `Hash` is the trusted 20-byte client integrity hash; it is **not** the
20-byte proof sent on the wire. The server compares the proof with
`SHA1(clientPublicKey || configuredHash)` in constant time. A zero hash leaves
that client tuple unchecked; an unlisted tuple is refused when strict checking
is enabled. A reconnect proof is always checked against `SHA1(R1 || 20 zero bytes)`
when strict checking is on, whatever is configured, as vmangos does. No hashes ship in the default configuration, so proxied clients
and existing installations retain the previous behavior.

No real client acceptance run or measured PIN-entry user experience is recorded
for this lane. The tests cover the realm wire protocol through loopback sockets
and the synthetic MockClient; they do not verify client file contents for a
specific local WoW installation.
