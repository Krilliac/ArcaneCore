# Vanilla wire oracle (2026-10-08)

Worktree base: `c3102f4404e792714222b42f5a05491f0e3d8500`, branch `codex/w3-wire-oracle`. Written by the Codex wave 3 lane; reviewed, fixed and committed at intake (see "Intake" below). Nothing is pushed.

## What changed

- Vendored 896 C# source files from gtker/wow_messages_csharp commit `b6839f735cebda217db177a2be6c6481d89fcac3` under `tests/ThirdParty/WowWorldMessages/src`: all `vanilla` models and the shared types they need. Every source file matches the upstream bytes except `ReadUtils.cs`, whose Wrath-only achievement helpers are disabled. `Compat/` supplies a plaintext header adapter and the small OneOf API the generated models use, so no NuGet download is required. `LICENSE-MIT.txt` carries the chosen license text; root `THIRD_PARTY_NOTICES.md` records the source, commit, license and files. Both project references are test-only.
- Added the optional `IOutboundPacketObserver` hook at `WorldSession.Send` and the managed-session capture path. `WorldTestHost` registers `WireOracle` only when `ARCANECORE_TEST_WIRE_ORACLE=1`; it asserts the collected errors at disposal. The observer builds a plaintext vanilla frame for wowm's `ServerOpcodeReader`, then requires the reader to consume every payload byte. `ARCANECORE_TEST_WIRE_ORACLE_HISTOGRAM` selects the histogram output path.
- Added 116 upstream `WowWorldMessages.Test/Vanilla.cs` round-trip byte vectors to MockClient.Tests (60 CMSG, 56 SMSG/MSG). CMSG vectors also verify MockClient's client-header encoder. The three upstream cases that recompress data compare the decoded message after the write, as upstream does, because zlib may produce different bytes for the same body.
- Changed three test fixtures (`PlayerbotMovementControlTests` selective drain, `StalledWriterTests`, `WriterTeardownBoundTests`) to send valid packet bodies of the same relevant sizes instead of malformed `SMSG_PONG`/quest-detail bytes. Corrected the existing `World:Playerbots:MovementPackets` documentation key so the World docs audit passes.

## Coverage and results

The final full World suite with `ARCANECORE_TEST_WIRE_ORACLE=1` passed **2,966**, skipped **21**, failed **0** (1m57s). Its [histogram](wire-oracle-histogram-20261008.tsv) records **46,900** outbound frames across **233** distinct opcode values: **204 distinct `SMSG_*`** and **29 server `MSG_*`**. Upstream defines exactly **300** `SMSG_*.cs` vanilla source models. Of the 204 emitted SMSGs, **198** match one of those models (ArcaneCore's `SmsgAccountDataMd5` is wowm's `SMSG_ACCOUNT_DATA_TIMES`) and **6** have no wowm model (`SMSG_MESSAGECHAT`, `SMSG_INVENTORY_CHANGE_FAILURE`, `SMSG_UPDATE_ACCOUNT_DATA`, `SMSG_BATTLEFIELD_WIN/LOSE/STATUS`); **102** wowm SMSG models were not exercised. Of the 233 emitted opcode values, 192 decoded wholly through wowm, 36 used an explicit model-gap entry, and 5 use both wowm and a narrow vmangos validator depending on the packet form. The final `failures.tsv` is empty.

`dotnet test tests/ArcaneCore.MockClient.Tests/ArcaneCore.MockClient.Tests.csproj -c Release -m:1 -nodeReuse:false --no-restore` passed **464/464** (7m31s), including all **116/116** imported vectors. `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false` succeeded with **0 warnings, 0 errors** (45s). Local ignored logs: `artifacts/wire-oracle/{world,mockclient,build}.log`. The World tests use in-process/loopback synthetic data; no real 1.12.1 client was run. No ClassicDB content row or schema change is needed for the oracle.

## Model gaps and provenance

The canonical, commented allowlist is `tests/ArcaneCore.World.Tests/WireOracle.cs` (38 entries, 36 emitted in this run). Every other reader exception or trailing byte fails the host. The entries represent these verified differences:

| Packet family | wowm gap | 1.12.1 reference |
| --- | --- | --- |
| `SMSG_MESSAGECHAT`, guild decline, inventory-change failure, account-data response, battlefield win/lose/status | No vanilla server-reader model | vmangos `Chat/Chat.cpp:ChatHandler::BuildChatPacket`, `Server/Packets/{Guild,Item,Misc,Battleground}.cpp` packet writers |
| Server `MSG_MOVE_*` relays and `MSG_TABARDVENDOR_ACTIVATE` | No server-reader arm | vmangos `Movement/MovementPacketSender.cpp` relay senders; `Handlers/GuildHandler.cpp:HandleTabardVendorActivateOpcode` |
| Force root/unroot and spline root | wowm uses an unpacked GUID | vmangos `Movement/MovementPacketSender.cpp:SendMovementFlagChangeToController/SendMovementFlagChangeToAll` writes packed GUIDs for build 5875 |
| Faction visible/standing | wowm uses 16-bit list IDs | vmangos `Server/Packets/Misc.cpp:SetFactionVisible/SetFactionStanding::AppendBodyTo` uses 32-bit IDs |
| Transfer abort, cast result | wowm adds map/argument; reverses the success/failure reason branch | vmangos `Server/Packets/Misc.cpp:TransferAborted::AppendBodyTo`, `Server/Packets/Spell.cpp:CastResult::AppendBodyTo` |
| Friend status, group list/member stats, guild event, channel notice | wowm omits variant suffixes, a group difficulty byte, the 16-bit zone/negative-aura layout, affected GUIDs or per-notice fields | vmangos `Server/Packets/{Social,Group,Guild}.cpp`, `Handlers/GroupHandler.cpp:BuildPartyMemberStatsPacket`, `Chat/Channel.cpp:Make*` |
| Quest/game-object query responses | wowm uses 16-bit reputation faction IDs and six game-object raw fields | vmangos `Server/Packets/Quest.cpp:QuestQueryResponse::AppendBodyTo` uses 32-bit faction IDs; `Server/Packets/Query.cpp:GameObjectQueryResponse::AppendBodyTo` writes 24 raw fields in 1.12.1 |
| Resurrection request, XP gain, periodic aura log, loot response | wowm omits delayed resurrection, reverses the kill-XP suffix, narrows school to one byte, and reads only six bytes per loot item | vmangos `Server/Packets/Spell.cpp:ResurrectRequest::AppendBodyTo`, `Server/Packets/Misc.cpp:LogXpGain::AppendBodyTo`, `Objects/Unit.cpp:SendPeriodicAuraLog`, `Server/Packets/Loot.cpp:operator<<(LootSlotItem)` |

Five opcodes use a conditional exception while other instances still go through wowm: `SMSG_MONSTER_MOVE` stop packets (vmangos `Movement/spline/MoveSplineInit.cpp:Launch`); transport `MSG_MOVE_HEARTBEAT` and living `SMSG_UPDATE_OBJECT` movement blocks (vmangos `MovementInfo.h`, `Objects/Object.cpp:BuildMovementUpdate`, 0x02000000 flag and fixed 24-byte transport block); and `SMSG_SPELL_START/GO` with a shared UNIT+CORPSE/GAMEOBJECT GUID or reflected-miss result (vmangos `Spells/SpellCastTargetsInfo.cpp:SpellCastTargets::write`, `Server/Packets/Spell.cpp:SpellGo::AppendBodyTo`). The test-only validators consume the complete alternate body and reject truncation or trailing data.

## Findings and limits

The suite found **no ArcaneCore production wire-layout defect** after each observed discrepancy was checked against vmangos. No production packet builder was changed. The production change is only the optional observer hook; the other edits are test code, vendored test source and documentation. The earlier switched-suite failures were wowm model gaps and deliberately malformed bytes in two pre-existing stress/drain fixtures. The latter fixtures now use valid messages while retaining their queue/drain assertions.

This is loopback evidence over the packets the World suite emitted. 102 upstream SMSG source models were not represented by a distinct emitted SMSG in this run, and the 36 emitted allowlisted opcode values receive only the stated structural checks where a model is missing or wrong. A real-client trace and broader scenarios would be needed to claim full 1.12.1 wire compatibility. The offline OneOf/header adapters were verified against the imported vectors, not against the upstream package binaries.

## Intake (2026-10-08)

Review fixes on top of the Codex diff:

- `WorldTestHostObservesAuthChallenge` passed vacuously: an oracle that was never called also has no failures. `WireOracle.ObservedCount` now lets it assert the auth challenge reached the oracle, and the new `ManagedSessionSendsReachTheOracle` does the same for the managed (bot) send path and checks that a malformed managed send fails the oracle.
- The histogram and `failures.tsv` are written to a temporary file and moved into place. Under heavy machine load the test host was killed during the ProcessExit write and left an empty `histogram.tsv`, which reads like "nothing emitted".
- `THIRD_PARTY_NOTICES.md` said only World.Tests referenced the vendored project (MockClient.Tests does too) and referred to MIT text "below" that lives in `LICENSE-MIT.txt`.
- Codex's build outputs inside `tests/ThirdParty/WowWorldMessages/{bin,obj}` were deleted before the native build (they are gitignored).

Native verification (Release, `dotnet build ArcaneCore.slnx -c Release -m:1 -nodeReuse:false`: 0 warnings, 0 errors):

- `WireOracleTests` 10/10. Proven able to fail: with both outbound hooks and the trailing-byte check removed, 4 of 10 fail (auth challenge, managed path, trailing byte, transport create); with the loot item size, force-root counter and faction-standing entry size altered, 3 of 10 fail.
- `VanillaRoundTripVectorsTests` 116/116; all 116 hex vectors were compared byte for byte with upstream `WowWorldMessages.Test/Vanilla.cs`. All 896 vendored `src` files except `ReadUtils.cs` are byte-identical to upstream.
- `RepoHygiene` (Game.Tests, which scans the tree for GPL-derived or proprietary data) 21/21 with the vendored tree present.
- Full World suite with `ARCANECORE_TEST_WIRE_ORACLE=1`, three runs on a shared, heavily loaded machine: 2964-2966 passed, 21 skipped, 1-2 failed each time, with no oracle failure (`failures.tsv` empty in every run). The failures were different scenario/timing tests each run (`QuestCommandTests`, `PartyScenarioTests`, `AuctionOutbidScenarioTests`, `PlayerbotScenarioTests.Trade_ItemForGold`, `PlayerbotQuestGoalsTests`), all timeouts or persistence races, and every one passed on isolated reruns (2-3 times each with the oracle on). The default run (oracle off) also had one such failure (`PartyScenarioTests`). The rerun reproduced the same 233-opcode set as the committed histogram.
