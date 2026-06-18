# SRP6 known-answer vectors

These `calculate_*.txt` files are copied verbatim from
[gtker/wow_srp](https://github.com/gtker/wow_srp) (`tests/srp6_internal/`), which is
dual-licensed **MIT OR Apache-2.0**. They are reverse-engineered from the real WoW
client and used here to verify ArcaneCore's SRP6 implementation byte-for-byte.

All hex values are **big-endian**. Column layouts (whitespace-separated):

| File | Columns |
|------|---------|
| `calculate_v_values.txt` | username, password, salt, verifier |
| `calculate_xor_hash.txt` | generator, prime N, xor-hash |
| `calculate_B_values.txt` | verifier, server private b, public B |
| `calculate_u_values.txt` | A, B, u |
| `calculate_S_values.txt` | A, verifier, u, server private b, S |
| `calculate_interleaved_values.txt` | S, session key K |
| `calculate_server_session_key.txt` | A, verifier, server private b, session key K |
| `calculate_M1_values.txt` | username, session key K, A, B, salt, M1 |
| `calculate_M2_values.txt` | A, M1, session key K, M2 |
