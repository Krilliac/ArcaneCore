# Lanternfall frame codec boundary

`LanternfallFrameCodec` mirrors the shared frame contract: a six-byte
little-endian header (`u32 size`, counting the `u16` opcode and payload), a 16 KiB
client frame limit and a 1 MiB server frame limit. It is a byte-level codec only.

It is intentionally not registered in `ClientProtocols`. A usable Lanternfall
profile still needs its generated opcode map and field layout, token authentication
codec/store, TLS listener binding, and typed payload handlers. Keeping the codec
unregistered prevents an incomplete profile from being accepted by a production
listener.

Local qualification on 2026-11-06 used the SDK pinned by `global.json`
(`11.0.100-rc.2.26504.105`) and locked package restores. The focused World suite
passed 64 tests (framing, handshake, header cipher, seams and packet limits), and
MockClient protocol/replay selection passed 74 tests. A targeted independent
review covered profile/cipher binding, bounds before allocation and wire parity.
This is focused local evidence; full hosted CI and a live Lanternfall TLS session
are separate gates.
