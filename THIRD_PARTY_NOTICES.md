# Third-party notices

## WoWDBDefs build-5875 DBC definitions

`src/ArcaneCore.Data/ClientData/ClientDbcLayouts.Dbd.g.cs` contains metadata derived from
`WoWDBDefs/definitions/*.dbd` at commit `e3df370`. The definitions are licensed
CC BY-SA 4.0 by the WoWDBDefs contributors. Attribution and license text:
https://github.com/wowdev/WoWDBDefs/blob/e3df370/LICENSE.md and
https://creativecommons.org/licenses/by-sa/4.0/ . The generated metadata is
an adaptation of those definitions and is distributed under CC BY-SA 4.0.
The source `.dbd` files are not included.

WoWDBDefs repository code has a separate BSD-3-Clause license; no code was copied.
`tools/codegen/gen_dbc_layouts.py` is an original parser of the definition format.

## Realm PIN and client integrity algorithms

- Repository: `gtker/wow_srp_csharp` (https://github.com/gtker/wow_srp_csharp), local snapshot at `D:/ArcaneCore-lanes/_refs/csharp-cores/wow_srp_csharp`
- Commit: `85ad800a0957fcf64621d640430f3b19af36490c`
- License: MIT OR Apache-2.0 (upstream dual license, `WowSrp/WowSrp.csproj` `PackageLicenseExpression` and `README.md`); ArcaneCore uses it under the MIT terms below
- Source files studied: `WowSrp/src/Pin.cs`, `WowSrp/src/Internal/PinImplementation.cs`, `WowSrp/src/Integrity.cs`; regression vectors from `WowSrp.Test/tests/pin/regression.txt`, `WowSrp.Test/tests/integrity/generic_regression.txt` and `WowSrp.Test/tests/integrity/reconnect_regression.txt` (first three rows of each), decoded as in `WowSrp.Test/src/TestUtils.cs`
- Derived ArcaneCore files: `src/ArcaneCore.Cryptography/PinHash.cs`, `src/ArcaneCore.Cryptography/ClientIntegrity.cs`, and the vector tests in `tests/ArcaneCore.Realm.Tests/PinIntegrityVectorTests.cs`

The PIN grid permutation and salt/hash sequence are adapted from the above source. The
client file checksum helper follows `Integrity.GenericCheck`. Server-side `crc_hash`
verification follows vmangos `AuthSocket::VerifyVersion` and uses a configured integrity
hash; vmangos source was consulted for behavior only and no GPL code was copied.

The upstream snapshot ships no license file; the MIT license text it refers to is:

```
MIT License

Copyright (c) Gtker and the wow_srp_csharp contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## DotRecast (test oracle only)

- Repository: https://github.com/ikpil/DotRecast
- Commit: `6fce4ac0d59f4d4efdf9de0a9e5beae5784bca12`
- License: Zlib; the unmodified license text is at [tests/ThirdParty/DotRecast/LICENSE.txt](tests/ThirdParty/DotRecast/LICENSE.txt).
- Imported files: the 61 C# files under `src/DotRecast.Core/` and 65 C# files under `src/DotRecast.Detour/`, copied without source edits to the corresponding `tests/ThirdParty/DotRecast/` projects. The project files are ArcaneCore's test-only wrappers.
- Purpose: independent Detour format/path query cross-check in `ArcaneCore.Game.Tests`. Production projects do not reference these assemblies.

## WowWorldMessages vanilla test oracle

- Upstream: gtker/wow_messages_csharp, commit `b6839f735cebda217db177a2be6c6481d89fcac3`.
- Upstream project declares `MIT OR Apache-2.0` in `WowWorldMessages/WowWorldMessages.csproj`. This copy uses the MIT option; the license text is in [tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt](tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt).
- Imported source: `WowWorldMessages/src/vanilla/*.cs`, `WowWorldMessages/src/all/*.cs`, and the vanilla-relevant shared files from `WowWorldMessages/src/*.cs` into `tests/ThirdParty/WowWorldMessages/src/`.
- Imported test fixtures: 116 byte vectors from `WowWorldMessages.Test/Vanilla.cs`, embedded in `tests/ArcaneCore.MockClient.Tests/VanillaRoundTripVectorsTests.cs` (the upstream NUnit test code itself was not copied).
- Test build adaptations: Wrath-only achievement helpers in `ReadUtils.cs` are disabled, the TBC/Wrath directories and helpers (`tbc/`, `wrath/`, `Tbc*`, `Wrath*`, `AddonArray.cs`, `InspectTalentGearMask.cs`) are not imported, and local `Compat` files replace the unavailable OneOf and WowSrp header dependencies for plaintext test decoding. These adapters are ArcaneCore code, not upstream imports.
- Scope: referenced only by the test projects `ArcaneCore.World.Tests` (the opt-in wire oracle) and `ArcaneCore.MockClient.Tests` (the round-trip vectors). No production project under `src/` or `tools/` references it.

The upstream source repository contains no standalone license file at this commit; its project metadata names both license options and `Copyright (c) Gtker 2024`. `tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt` holds the MIT text for the chosen option with that copyright.
