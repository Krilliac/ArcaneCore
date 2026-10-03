# Protocol vector provenance

The numerical SRP6 and rolling-header-cipher vectors embedded in `ProtocolTests.cs`
are selected from **gtker/wow_srp**, commit
`25ffab6433e1ee5eee629200cf42b592c1f36121`:

- `tests/srp6_internal/calculate_A_values.txt` (first two rows)
- `tests/srp6_internal/calculate_client_S_values.txt` (first two rows)
- `tests/srp6_internal/calculate_interleaved_values.txt` (first two rows)
- `tests/srp6_internal/calculate_M1_values.txt` and `calculate_M2_values.txt` (first row)
- `tests/encryption/calculate_world_server_proof.txt` (first row)
- `src/vanilla_header/mod.rs` (`verify_client_header`, `verify_server_header`, captured session key and proof)

SRP vector integers, session keys, and the world-proof vector digest are printed as
big-endian hexadecimal upstream; the tests reverse them into fixed-width little-endian
fields. In particular, upstream `src/hex.rs::hex_decode_be` reverses byte pairs, as used
by `verify_seed_proof` in `src/vanilla_header/mod.rs`. Literal header captures and their
captured proof bytes preserve their original byte order. The empty-addon and pong
continuation in the socket test was calculated independently for the captured key.
These are upstream test fixtures. Their original real-client capture provenance
has not been independently established here, and they do not replace real-client
acceptance evidence.

The packet layout was cross-checked with **gtker/wow_messages**, commit
`70abb9deff0bb63440d8aeb4386b820653e8a176`,
`examples/vanilla_client/src/auth.rs` and `server.rs`. Runtime client code was
written independently; it does not call the server's SRP6 or header cipher.

Source: https://github.com/gtker/wow_srp/tree/25ffab6433e1ee5eee629200cf42b592c1f36121

The upstream project offers MIT OR Apache-2.0. These vector excerpts are used under MIT:

Copyright 2020 Gtker

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE
OR OTHER DEALINGS IN THE SOFTWARE.
