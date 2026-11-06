# Lanternfall HTTPS login

`src/ArcaneCore.Login` hosts `/v1/login` using the client's account, password,
optional TOTP, `protocol_version` and `build_id` JSON contract. It returns the
existing one-time token/seed and realm-list shape. This is separate from vanilla
SRP6 and does not enable the Lanternfall world TLS listener.

The host requires a private-key certificate and auth database configuration.
It replaces Kestrel's default endpoint loader with a controlled TLS listener;
additional configured HTTP endpoints cannot expose the credential route. TLS 1.2
and 1.3 remain enabled, with no plaintext fallback. Windows uses the key import
mode required by Schannel and disposes the certificate when the application ends.

Auth v9 stores credentials; v10 adds the Lanternfall realm protocol/TLS endpoint
columns; v11 stores accepted active/grace builds. Existing realms default to
protocol zero and are not offered to the client. Provision production protocol
and build values from the release manifest; no release IDs are assumed here.
Run the normal stopped-daemon, backed-up schema upgrade before starting the host.

Passwords use a pinned Argon2id implementation and PHC strings. TOTP secrets are
AES-GCM sealed under configured host-secret keys, never a built-in key. The login
service alone registers the credential store. It checks verified email, account
state, persistent failure lockout, staff TOTP, replay steps, bans and eligible
realm/build rows before token issuance. The world must repeat its own release,
ban and session-admission checks.

Validation includes the Argon2 RFC 9106 vector, crypto/store tests, and a real
Kestrel/SQLite login exercised through certificate-pinned loopback TLS. The test
also rejects an injected plaintext listener. Local provider execution is SQLite;
MariaDB/PostgreSQL execution and full hosted CI remain separate gates. Website
registration/recovery, deployment secrets/certificates and the world TLS/profile
adapter remain further launch work.
