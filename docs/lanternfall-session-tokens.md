# Lanternfall persistent session tokens

The auth v8 `session_token` table implements the token persistence contract in
Game's `schema/db/new/auth.yaml`. `SessionTokenService` generates a random 16-byte
ID, 32-byte secret and 32-byte integrity seed for a 60-second login ticket. Only
SHA-256 of the token secret is stored. The integrity seed remains in the auth
database so the world can derive the session's integrity keys.

Consumption checks the secret hash in fixed time, expiry, build and canonical IP.
Resume tokens additionally require the issuing realm. A conditional update inside
a serializable transaction consumes the token once, removes the account's older
unconsumed resumes and inserts a five-minute replacement. The replacement retains
the original login token ID and integrity seed. Consumed rows remain for audit.
A failed replacement rolls back consumption. Known database contention permits
three attempts; an exhausted transient failure propagates as a database failure
instead of being reported as invalid credentials.

Tickets and authentication results own their secret/seed buffers and clear them
on disposal. Getters return copies owned by the caller; callers must dispose the
objects and clear their own copies when finished. No secret values are logged.

`RevokeUnconsumedAsync` provides the persistence operation for sign-out/recovery.
`PurgeExpiredAsync` takes an explicit UTC cutoff; the future maintenance caller
must supply `now - 24 hours` for the designed audit retention. Neither operation
is scheduled by this change.

## Integration boundary

This is not a credential verifier or an enabled world-login endpoint. The future
HTTPS login service must verify credentials, second factors, account state and
release eligibility before issuing a ticket. The world must independently check
account status, bans, accepted protocol/build and session admission, including its
existing post-claim recheck. Do not use the legacy SRP6 session-key column for these
tokens. TLS listener/profile binding and generated wire handlers remain separate
integration work.

The EF module is discovered by the existing auth database composition. Deploy the
generated, version-guarded auth v8 update for the chosen provider through the
normal stopped-daemon database-upgrade runbook. No account credential table or
additional database privileges are introduced here.

## Qualification

Local verification used the pinned .NET 11 SDK and locked restores: 16 focused
token tests, 54 SQL/schema tests, and the full Data suite (2,369 passed, 68 skipped,
zero failures). The generated SQL covers all three providers; runtime execution
was SQLite only because MariaDB/PostgreSQL test connections were not configured.
Those provider executions and full hosted CI remain acceptance gates. A targeted
independent security review covered transaction races, replay, rollback and
secret ownership; this does not claim a Daybreak-specific backend execution.
The final focused run additionally forces a replacement save to fail after the
claim and deletion, then verifies rollback and a successful retry on a fresh
context. The full Data count precedes that additional test; production code is
unchanged between the two runs.
