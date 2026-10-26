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

- Repository: `gtker/wow_srp_csharp` (https://github.com/gtker/wow_srp_csharp), read from a local read-only snapshot
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

## DotRecast (test and visualizer oracle only)

- Repository: https://github.com/ikpil/DotRecast
- Commit: `6fce4ac0d59f4d4efdf9de0a9e5beae5784bca12`
- License: Zlib; the unmodified license text is at [tests/ThirdParty/DotRecast/LICENSE.txt](tests/ThirdParty/DotRecast/LICENSE.txt).
- Imported files: the 61 C# files under `src/DotRecast.Core/` and 65 C# files under `src/DotRecast.Detour/`, copied without source edits to the corresponding `tests/ThirdParty/DotRecast/` projects. The project files are ArcaneCore's test-only wrappers.
- Purpose: independent Detour format/path query cross-check in `ArcaneCore.Game.Tests`, `tools/ArcaneCore.Atlas` (`Reference/Navmesh/` and `Diff/NavDiff.cs`), and `tests/ArcaneCore.Atlas.Tests`. Production server projects under `src/` do not reference these assemblies.

## WowWorldMessages vanilla test oracle

- Upstream: gtker/wow_messages_csharp, commit `b6839f735cebda217db177a2be6c6481d89fcac3`.
- Upstream project declares `MIT OR Apache-2.0` in `WowWorldMessages/WowWorldMessages.csproj`. This copy uses the MIT option; the license text is in [tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt](tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt).
- Imported source: `WowWorldMessages/src/vanilla/*.cs`, `WowWorldMessages/src/all/*.cs`, and the vanilla-relevant shared files from `WowWorldMessages/src/*.cs` into `tests/ThirdParty/WowWorldMessages/src/`.
- Imported test fixtures: 116 byte vectors from `WowWorldMessages.Test/Vanilla.cs`, embedded in `tests/ArcaneCore.MockClient.Tests/VanillaRoundTripVectorsTests.cs` (the upstream NUnit test code itself was not copied).
- Test build adaptations: Wrath-only achievement helpers in `ReadUtils.cs` are disabled, the TBC/Wrath directories and helpers (`tbc/`, `wrath/`, `Tbc*`, `Wrath*`, `AddonArray.cs`, `InspectTalentGearMask.cs`) are not imported, and local `Compat` files replace the unavailable OneOf and WowSrp header dependencies for plaintext test decoding. These adapters are ArcaneCore code, not upstream imports.
- Scope: referenced by `ArcaneCore.World.Tests` (the opt-in wire oracle and capture tests), `ArcaneCore.MockClient.Tests` (round-trip vectors), and `tools/ArcaneCore.Sniff` (offline decode). No production project under `src/` references it.

The upstream source repository contains no standalone license file at this commit; its project metadata names both license options and `Copyright (c) Gtker 2024`. `tests/ThirdParty/WowWorldMessages/LICENSE-MIT.txt` holds the MIT text for the chosen option with that copyright.

## Sonder-Inference sampling module (ported)

- Repository: Krilliac/Sonder-Inference (https://github.com/Krilliac/Sonder-Inference), read from a local read-only snapshot
- Commit: `1c4c152acc0be1f4f811c60e8c20b17cfb794b0b`
- License: Apache-2.0 (`LICENSE` in the upstream repository; upstream `NOTICE`: "Sonder-Inference, Copyright 2026 Nate Witkowski")
- Source files ported: `src/sampling/include/sonder/sampling/{types,rng,config,constraint,sampler,samplers,chain,stop}.hpp`, `src/sampling/src/{types,rng,config,constraint,samplers,chain,stop}.cpp`, and the unit tests `src/sampling/tests/test_{rng,stop,config,samplers,chain,constraint}.cpp` (rewritten for xUnit)
- Derived ArcaneCore files: `src/ArcaneCore.Playerbots/Chat/Inference/Sampling/` (`SamplingTypes.cs`, `SamplingRng.cs`, `SamplerConfig.cs`, `IConstraint.cs`, `Samplers.cs`, `SamplerChain.cs`, `StopSequenceMatcher.cs`) and `tests/ArcaneCore.World.Tests/Playerbots/Chat/Inference/Sampling/` (all but `ChatLineConstraintTests.cs`). Each derived file carries a "Ported from" header. `ChatLineConstraint.cs` and its tests are original ArcaneCore code.
- Changes from upstream: rewritten in C#; candidates live in a reused buffer, typical-p scratch is kept between calls, logit bias/penalties index by token id while candidates are in vocabulary order, greedy and t = 0 pick the canonical argmax in one pass instead of a full sort, and stop sequences are matched on UTF-8 bytes without per-piece allocation. Seeds and selection order are unchanged, so a seed yields the upstream tokens (the seed-42 golden sequence is pinned in the tests). `core_bridge` (mapping Sonder's core config and status types) was not ported.
- Upstream notes its stage semantics follow llama.cpp's documented sampler behaviour (`tools/completion/README.md`, MIT) as an independent implementation; no llama.cpp code is included here.

## Sonder-Inference scheduler, KV cache and session lifecycle (bot chat inference)

- Repository: Krilliac/Sonder-Inference (https://github.com/Krilliac/Sonder-Inference), read from a local read-only clone
- Commit: `1c4c152acc0be1f4f811c60e8c20b17cfb794b0b`
- License: Apache-2.0 (https://www.apache.org/licenses/LICENSE-2.0). Upstream NOTICE: "Sonder-Inference, Copyright 2026 Nate Witkowski. This product is licensed under the Apache License, Version 2.0." No upstream third-party component (llama.cpp, cpp-httplib, doctest) was ported.
- Source files ported to C#: `src/scheduler/include/sonder/inference/scheduler/*.hpp` and `src/scheduler/src/*.cpp` (types, clock, kv_capacity, config, stats, scheduler, simulator); `src/cache/include/sonder/inference/cache/*.hpp`, `src/cache/types.cpp`, `src/cache/eviction_policy.cpp`, `src/cache/kv_cache_manager.cpp`; `src/engine/kv_capacity_adapter.hpp`; lifecycle ideas from `src/sessions/session.cpp` and `src/engine/request_runtime.hpp`.
- Derived ArcaneCore files: `src/ArcaneCore.Playerbots/Chat/Inference/Scheduling/` (including `Cache/` and `Sessions/`); each file carries a "Ported from" header naming its source path and commit. Unit tests ported from `src/scheduler/tests/*.cpp` and `src/cache/tests/*.cpp` live in `tests/ArcaneCore.World.Tests/Playerbots/Chat/Inference/Scheduling/`.
- Changes: idiomatic C# rewrite; the seven upstream workload classes are replaced by ArcaneCore's `InteractivePlayer`, `BotToBot` and `Ambient`; droppable (Ambient) requests that wait past a queue deadline are dropped; the upstream `Status` type becomes `KvCacheStatus`; `InferenceScheduler.PlanStep(StepPlan)` refills a reused plan so the engine loop allocates nothing per step. The port runs in-process and calls no Sonder or Ollama service.
- Integration (original ArcaneCore code, not a port): `src/ArcaneCore.Playerbots/Chat/Inference/Serving/` (`SlotKvCapacity`, `KvPrefixStore`, `ChatReplyDecoder`, `ChatServingStats`) wires the ported sampler chain, scheduler and KV block manager into the in-process chat engine (`InProcessChatEngine`, `ChatInferenceContext`).
