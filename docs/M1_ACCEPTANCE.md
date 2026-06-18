# M1 Acceptance Test — Logon + SRP6

> Defined against a **real WoW 1.12.1 client, build 5875**. Per Charter §1.3 /
> §6, M1 is "done" only when this passes against that client — not when the code
> compiles or unit tests pass.

## Scope under test

The ArcaneCore **logon/realm daemon** (`ArcaneCore.Realm`) only:

- Listens on **TCP 3724**.
- Handles `CMD_AUTH_LOGON_CHALLENGE` (0x00), `CMD_AUTH_LOGON_PROOF` (0x01),
  and `CMD_REALM_LIST` (0x10).
- Performs the WoW-flavored **SRP6** exchange (g=7, k=3, well-known prime N),
  validating the client's proof `M1` and returning `M2`.
- Returns a realm list containing the configured ArcaneCore realm.
- Supports **WCell-style auto-create-on-login** when enabled (see below).

Out of scope for M1 (does not exist yet): the world daemon, header encryption,
character handling. Selecting the realm will attempt a world connection that
correctly does not complete — that is expected at M1.

## Pre-conditions

1. A MariaDB (or MySQL / PostgreSQL) instance is reachable with the schema
   created by ArcaneCore migrations.
2. The realm daemon is configured and running on 3724; a realm row exists (or is
   seeded) pointing `address` at the (future) world daemon, e.g. `127.0.0.1:8085`.
3. One of:
   - **Pre-created account** via `ArcaneCore.AccountTool` (username + password), or
   - **Auto-create enabled** (`Auth:AutocreateAccounts = true`).
4. The client's `realmlist.wtf` contains `set realmlist 127.0.0.1` (or the host
   running the daemon).

## Procedure

1. Launch the real 1.12.1 (5875) client.
2. At the login screen, enter credentials:
   - If using a pre-created account: that username/password.
   - If using auto-create (WCell convention): enter the desired **username** and
     use **that same username as the password**. On the first valid login the
     account is created.
3. Submit login.

## Expected result (PASS criteria)

- **No** "Unable to connect" — the daemon accepted the TCP connection on 3724.
- **No** "Incorrect password" / "Unable to validate game version" — the SRP6
  proof `M1` validated and the daemon returned a matching `M2`.
- The client advances past the login screen to the **realm-list screen**.
- The configured **ArcaneCore realm is listed** and selectable.
- (Auto-create case) the account row now exists in the DB with a stored salt and
  verifier; logging in again with the same credentials succeeds.

Reaching the realm-list screen with the ArcaneCore realm visible = **M1 PASS**.

## Negative checks

- Wrong password (pre-created account) → client shows "Incorrect password"; no
  session is established. (For an auto-create attempt, a password that does not
  equal the username simply fails and no account is created.)
- Banned/suspended account status (if set) → corresponding client error.

## Evidence to capture

- Screenshot of the client at the realm-list screen showing the ArcaneCore realm.
- Daemon log lines for: challenge received, proof validated, realm list sent.
- DB row for the (auto-)created account showing non-null salt + verifier.

## Reference basis for the protocol under test

Packet layouts and SRP6 math verified against `gtker/wow_messages` +
`gtker/wow_srp` (machine-readable, neutral) and cross-checked against
`vmangos` / `cmangos-classic` `AuthSocket`/`SRP6`. The auto-create-on-login
mechanism is modeled on `WCell` (`Services/WCell.AuthServer/Authentication.cs`).
Discrepancies found during implementation are recorded in `MILESTONE_M1.md`.
