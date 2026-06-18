# MILESTONE M1 — Logon + SRP6

Status: **implementation + automated verification complete; awaiting real-client
acceptance** (Charter §1.3 / §8: M1 is "done" only when an actual 1.12.1 client passes
`docs/M1_ACCEPTANCE.md`, which is the developer's gate and cannot be run in CI).

## What was built

- `ArcaneCore.Kernel` — domain models (`Account`, `RealmEntry`) and the data seams
  (`IAccountStore`, `IRealmStore`) where clustering will later slot in (Charter §5).
- `ArcaneCore.Cryptography` — WoW-flavor SRP6 (`WowSrp6`, `Srp6Math`, `Srp6Server`).
- `ArcaneCore.Data` — EF Core context + stores, provider-selectable across
  **MariaDB / MySQL / PostgreSQL**; schema via `EnsureCreated`; realm seeding.
- `ArcaneCore.Realm` — the logon/realm daemon: TCP listener on 3724, the
  challenge/proof/realm-list handlers, and WCell-style auto-create-on-login.
- `ArcaneCore.AccountTool` — CLI for `create` / `set-password` / `list`.

## Verified against (Charter §1.1 / §4)

| Detail | Reference |
|--------|-----------|
| Opcodes (0x00/0x01/0x10), result codes | vmangos `src/realmd/AuthCodes.h` |
| Challenge/proof client + server struct layouts | vmangos `src/realmd/AuthPackets.h`, `AuthSocket.cpp` |
| Realm-list (build &lt; 6299) layout | vmangos `AuthSocket.cpp` `LoadRealmlistAndWriteIntoBuffer` |
| `VersionChallenge` (crc_salt) 16-byte constant | vmangos `AuthSocket.cpp:65` |
| SRP6 N, g=7, k=3, B, u, S, M1, M2 | vmangos `SRP6.cpp`; gtker/wow_srp `srp_internal.rs` |
| SRP6 byte-for-byte values | gtker/wow_srp known-answer vectors (1000 cases/step) |
| Auto-create-on-login mechanism | WCell `Services/WCell.AuthServer/Authentication.cs:237-366` |

### Automated tests
- **8,005** SRP6 known-answer assertions (verifier, B, u, S, interleave, full session
  key, M1, M2 — 1000 vectors each — plus client⇄server round-trip and negative cases).
  Vectors copied from gtker/wow_srp (MIT/Apache) into the test project.
- **3** loopback integration tests driving the real `LogonSession` with a simulated
  client: full challenge→proof→realm-list exchange, auto-create, and rejection.

## Decisions & reference discrepancies found

1. **Hashing width — padded, not minimal.** Public keys A/B and N are hashed as fixed
   32-byte little-endian; the session key as 40 bytes; the salt as raw 32 bytes. vmangos
   hashes the *minimal* big-number representation, which differs only when a value has a
   leading zero byte (~1/256) — a latent rare-failure bug masked by login retries.
   gtker/wow_srp (reverse-engineered from the client) and several KAT rows with
   leading-zero keys confirm **padded** is the client-correct behavior. ArcaneCore uses
   padded and passes those vectors.

2. **Session-key interleave — strips even leading-zero pairs.** The canonical client
   algorithm strips an even number of leading (low-order) zero bytes from the
   little-endian S before splitting. vmangos simplifies to a fixed 32 bytes (identical
   for full-width S). ArcaneCore follows the stripping form (gtker `as_equal_slice`),
   verified against `calculate_interleaved_values.txt`.

3. **Realm-list trailing bytes = `0x0002`.** vmangos writes `uint16(0x0002)` after the
   realm array (not `0`, as some neutral docs state). ArcaneCore matches vmangos.

4. **Proof success response = 26 bytes** for build &lt; 6299: `cmd, error, M2[20],
   surveyId(u32)` (vmangos `AUTH_LOGON_PROOF_S`). Failure response = `cmd, error,
   uint16(0)`.

5. **Database engine.** MariaDB primary with MySQL + PostgreSQL support via EF Core
   (Pomelo + Npgsql). Schema is created with `EnsureCreated` rather than migrations: at
   M1 it is two small tables that must work identically on all three engines, where
   three migration sets would be premature. Revisit when the schema grows.

6. **Toolchain.** Targets **.NET 10 / C# 14** (latest), but the EF Core + Extensions
   packages are pinned to **9.0.0** because the Pomelo MySQL/MariaDB provider (the
   primary engine) has no .NET 10 release yet; EF 9 is runtime-compatible with net10.0.

## Client build verified against

Pending — to be filled in by the developer after running `docs/M1_ACCEPTANCE.md`
against a real WoW **1.12.1 (build 5875)** client.
