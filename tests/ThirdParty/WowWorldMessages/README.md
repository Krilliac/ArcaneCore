# Vanilla wire model used by tests

This is the `WowWorldMessages/src/vanilla` generated source at gtker/wow_messages_csharp commit `b6839f735cebda217db177a2be6c6481d89fcac3`, plus its `src/all` vector types and shared vanilla helpers. The upstream project declares `MIT OR Apache-2.0`; this copy uses MIT (see `LICENSE-MIT.txt` and the root `THIRD_PARTY_NOTICES.md`).

The original project targets .NET 8 and references WowSrp and OneOf packages. The local test build is offline and uses `Compat/Header.cs` for plaintext vanilla framing and `Compat/OneOf*.cs` for generated union values. It does not implement encrypted headers. The only source edit to the imported code is the `#if false` around Wrath achievement helpers in `src/ReadUtils.cs`. TBC and Wrath files are absent. The generated vanilla packet layouts and server opcode switch are unmodified.

Only `ArcaneCore.World.Tests` and `ArcaneCore.MockClient.Tests` reference this project. The World host observes payloads before header encryption; the opcode reader consumes a reconstructed plaintext vanilla header and must reach the end of the frame.
