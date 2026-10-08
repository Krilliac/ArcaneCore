# Third-party source notices

## WowWorldMessages vanilla test oracle

- Upstream: gtker/wow_messages_csharp, commit `b6839f735cebda217db177a2be6c6481d89fcac3`.
- Upstream project declares `MIT OR Apache-2.0` in `WowWorldMessages/WowWorldMessages.csproj`. This copy uses the MIT option; the license text is in [tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt](tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt).
- Imported source: `WowWorldMessages/src/vanilla/*.cs`, `WowWorldMessages/src/all/*.cs`, and the vanilla-relevant shared files from `WowWorldMessages/src/*.cs` into `tests/ThirdParty/WowWorldMessages/src/`.
- Imported test fixtures: 116 byte vectors from `WowWorldMessages.Test/Vanilla.cs`, embedded in `tests/ArcaneCore.MockClient.Tests/VanillaRoundTripVectorsTests.cs` (the upstream NUnit test code itself was not copied).
- Test build adaptations: Wrath-only achievement helpers in `ReadUtils.cs` are disabled, the TBC/Wrath directories and helpers (`tbc/`, `wrath/`, `Tbc*`, `Wrath*`, `AddonArray.cs`, `InspectTalentGearMask.cs`) are not imported, and local `Compat` files replace the unavailable OneOf and WowSrp header dependencies for plaintext test decoding. These adapters are ArcaneCore code, not upstream imports.
- Scope: referenced only by the test projects `ArcaneCore.World.Tests` (the opt-in wire oracle) and `ArcaneCore.MockClient.Tests` (the round-trip vectors). No production project under `src/` or `tools/` references it.

The upstream source repository contains no standalone license file at this commit; its project metadata names both license options and `Copyright (c) Gtker 2024`. `tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt` holds the MIT text for the chosen option with that copyright.
