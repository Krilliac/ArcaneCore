# Realm PIN, TOTP and integrity lane (2026-10-08)

Base: `c3102f44`. Written by the Codex lane; committed on `codex/w3-realm-pin-integrity` after
intake review (see "Intake" below). Nothing was pushed.

## Implemented

- Auth schema v5 adds `account.LockFlags`, `SecurityInfo` and `LastIp`, with an
  additive upgrade and administrative writes that revoke the stored session key.
- Build-5875 logon challenges emit the classic PIN flag, grid seed and salt for
  `IP_LOCK` on a changed address or `ALWAYS_ENFORCE`; proof parsing reads the
  optional 36-byte PINData and verifies fixed PIN or RFC 6238 TOTP before storing
  the world session key. Fixed PIN has priority if a legacy row has both bits.
  Behavior: vmangos `src/realmd/AuthSocket.cpp` account/IP policy 393-414,
  challenge 497-517, PINData 578-587, factor verification 689-738,
  `VerifyPinData` 1204-1272 and `GenerateTotpPin` 1274-1301; flag values in
  `src/realmd/AuthSocket.h` `LockFlag`. PIN hashing follows the permitted
  WowSrp import in `THIRD_PARTY_NOTICES.md`.
- `Auth:StrictVersionCheck` defaults off. Exact build/OS/platform entries supply
  a 20-byte integrity hash; an all-zero configured hash skips checking for that
  tuple. Strict logon uses `SHA1(A || configuredHash)` and strict reconnect uses
  `SHA1(R1 || 20 zero bytes)` with no configured entry needed, per vmangos `AuthSocket::VerifyVersion` at
  `src/realmd/AuthSocket.cpp:1433-1470` and its callers at 745 and 944.
  `ClientIntegrity.GenericCheck` follows WowSrp's client-file checksum helper.
- `arcane-account set-pin|set-totp|clear-pin|clear-totp|set-ip-lock` manages
  the account factor; the set commands accept a no-echo prompt or stdin.
  Administrator `.account pin|totp set|clear` generates the secret server-side
  so command text never carries it into the GM audit. `.account iplock` toggles
  address locking. Both surfaces refuse enabling an IP lock with no previous
  trusted address or factor.
- The MockClient reads the grid challenge and sends PINData; it can also send
  a configured integrity proof for loopback tests.

## Files in the diff

| Area | Paths |
|---|---|
| Algorithms | `src/ArcaneCore.Cryptography/{PinHash,Totp,ClientIntegrity}.cs` |
| Account model and store | `src/ArcaneCore.Kernel/Accounts/{Account,IAccountStore,IAccountLoginSecurityStore,AccountLoginSecurityPolicy}.cs`, `src/ArcaneCore.Kernel/Configuration/AuthOptions.cs`, `src/ArcaneCore.Data/Auth/AccountLoginSecurityDataModule.cs`, `src/ArcaneCore.Data/Stores/EfAccountStore.cs` |
| Realm wire | `src/ArcaneCore.Realm/Net/LogonSession.cs`, `src/ArcaneCore.Realm/Protocol/{LogonChallengeRequest,LogonProofRequest}.cs`, `src/ArcaneCore.Realm/Resilience/GuardedAuthStores.cs` |
| Operators and mock client | `tools/ArcaneCore.AccountTool/Program.cs`, `src/ArcaneCore.World/Gm/LogonSecurity/AccountLoginSecurityCommands.cs`, `tools/ArcaneCore.MockClient/Protocol/{LogonClient,ProtocolPackets}.cs` |
| Tests | `tests/ArcaneCore.Data.Tests/{AccountLoginSecurityUpgradeTests,IntegratedSchemaTests,ManagedPlayerbotProvisionTests}.cs`, `tests/ArcaneCore.Realm.Tests/{LogonHandshakeTests,LogonReconnectTests,PinIntegrityVectorTests,InMemoryStores}.cs`, `tests/ArcaneCore.Realm.Tests/Resilience/GuardedLoginAddressTests.cs`, `tests/ArcaneCore.World.Tests/InMemoryAccountStore.cs`, `tests/ArcaneCore.Realm.Tests/Bans/RealmBanEnforcementTests.cs`, `tests/ArcaneCore.Realm.Tests/Security/{RepeatedChallengeTests,StaleSrpTakeoverTests}.cs`, `tests/ArcaneCore.MockClient.Tests/RealmPinMockTests.cs`, `tests/ArcaneCore.World.Tests/Gm/AccountLoginSecurityCommandTests.cs` |
| Docs and attribution | `THIRD_PARTY_NOTICES.md`, `docs/security/realm-pin-integrity.md`, `docs/README.md`, `docs/reference/{configuration,gm-commands,schema}.md`, this report |

The previous realm security tests expected two padding bytes on failed proof
responses. vmangos sends only command and error for build 5875 (its padding is
for builds above 6005), so those test readers and the server response changed
together.

## Verification (Codex sandbox, before intake)

- Initial exact `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`
  stopped at restore with `NU1900` because the sandbox cannot reach the NuGet
  vulnerability feed. The same Release build with `-p:NuGetAudit=false` passed
  after RAM preflight: **0 warnings, 0 errors**. No dependency versions changed.
- Realm focused handshake/reconnect/vectors: **27/27 passed** before the final
  extra strict-off integrity case. Final full Realm: **292/292 passed**. One
  unrelated allocation assertion failed in an intermediate full run, passed
  alone, and the final full rerun passed.
- Data focused auth upgrade/integrated schemas: **7/7 passed**. Full Data:
  **1,313 passed, 12 skipped**. A legacy test expecting the old v4 head was
  corrected to expect the composed current version and the full run repeated.
- MockClient focused PIN/protocol: **70/70 passed**. Full MockClient:
  **349/349 passed**.
- World focused Administrator command: **5/5 passed**; Docs: **76/76 passed**;
  full World: **2,962 passed, 21 skipped**.
- Full Cryptography: **8,019/8,019 passed**; Kernel: **32/32 passed**.
- `git diff --check`: no whitespace errors.

The new tests were not executed against an unmodified baseline; the base
`LogonSession` rejects every nonzero security flag and does not check `crc_hash`.
The final green results above are from this lane's code. No real-client login,
hosted CI, database engine other than the locally available SQLite test
provider, or live-server run occurred. No ClassicDB content rows are needed;
strict client integrity requires an operator-supplied trusted hash for each
client tuple intended to be checked.

## Intake (2026-10-08)

Review fixes on top of the Codex diff:

- **IP_LOCK never saw a trusted address in the daemon.** `IAccountStore.UpdateLoginAsync` had a
  default body that fell back to `UpdateSessionKeyAsync`, and the realm's `GuardedAccountStore`
  decorator did not override it, so the composed daemon dropped the address and `LastIp` stayed
  empty. The member is now abstract and forwarded by the decorator and the test stores;
  `GuardedLoginAddressTests` composes `AddAuthDatabase` + `AddRealmResilience` over SQLite and
  checks the address lands, and the MockClient PIN test checks `LastIp` end to end.
- **Strict reconnect** needed a configured entry for the client's OS/platform; vmangos
  `VerifyVersion(isReconnect=true)` skips the build lookup. Reconnect now always checks
  `SHA1(R1 || 0^20)` when strict, and the reconnect test covers both configured and empty lists.
- A proof whose flags carry the PIN bit plus other bits now still reads its 36-byte PINData
  (vmangos reads it whenever `SECURITY_FLAG_PIN` is set) before refusing, so the stream stays framed.
- `THIRD_PARTY_NOTICES.md` carries the MIT text and the full commit id; the vector tests use three
  published rows each for PIN, generic integrity and (new) reconnect integrity.
- The GM command file moved to `Gm/LogonSecurity/` to match its namespace; `arcane-account`
  clear/ip-lock commands print usage and unknown-account errors instead of exiting 1 silently.
- The out-of-lane `docs/integration/wave6-20261008.md` key fix was reverted (the coordinator tracks
  that known `DocKeyAuditTests` failure), and the no-op `docs/reference/exit-codes.md` change dropped.
- The security page now records the deliberate leading-zero difference from vmangos.

Native results (`dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings,
0 errors): Realm **304/304**; MockClient **349/349**; Data **1,313 passed, 12 skipped**;
Cryptography **8,019/8,019**; Kernel **32/32**; World **2,961 passed, 21 skipped, 1 failed** (the
known pre-existing `DocKeyAuditTests` failure on `docs/integration/wave6-20261008.md:12`).
Fail-proof: with the reconnect, wrong-PIN, logon-integrity, address-recording and decorator
behaviours each broken, 5 of the 30 focused handshake/reconnect/decorator tests failed, one per break.
