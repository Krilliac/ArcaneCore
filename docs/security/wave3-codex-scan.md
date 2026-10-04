# Wave 3 Codex (Daybreak Blue) security diff scan: validation and disposition

Scan: `3bda712..7313b9e` (220 changed source items), Codex Daybreak Blue, branch-diff mode, coverage `complete`. The run logged
8 "Access is denied" sandbox errors; they were the sandbox refusing to copy or resolve scratch paths and the Daybreak access
advisory, not a coverage gap: `coverage.json` lists every surface with a disposition and `findings.json` holds 5 source-traced
findings. The scan was static only (no build, DB or client run). Every finding below was re-validated by hand against the code at
7313b9e; the scanner's verdicts were not taken on trust.

Result: 5 reported, 4 real (one only in part), 1 partly false positive. All real parts are fixed on `claude/cx-sec-fixes`, each
behind a setting that restores the vmangos behaviour, and each only differs from vmangos for malformed data or enormous listings.

| Id | Severity | Validated | Evidence at 7313b9e | Disposition |
|---|---|---|---|---|
| 1 Imported loot reference `maxcount` stalls the world thread | Medium | Yes | `LootGenerator.cs:89` loops `row.MaxCount` (uint, imported by `GameObjectLootDumpImporter.cs:519`) calling `Process`; `MaxReferenceDepth` bounds depth only, so repeats multiply per level | Fixed: `LootGenerator.MaxProcessCallsPerRoll` (default 4096, `0` = vmangos unbounded). Test `ImportedHugeReferenceMaxcount_IsBoundedByThePerRollBudget` |
| 2 Petition loading / signature packets | Medium | Partly | Real: `PetitionManager.Load` (`:82-91`) accepted any number of signatures although the sign path stops at `ClientMaxSignatures` (`:271`), and `PetitionPackets.cs:37` writes the count as a byte. False positive: materialising all `petition` rows at start (`PetitionDataModule.cs:94-96`) is the same load-everything-at-start pattern every world module uses; the data is operator-controlled | Fixed the real part: `Load` keeps at most `ClientMaxSignatures` signatures and persists the trim. Test `Load_CapsAnOversizedPersistedSignatureList_AtTheClientMaximum_AndPersistsTheTrim`. Start-up paging not done (design, not a vulnerability) |
| 3 Mail recipient cap stale / off by one | Medium | Race yes, off by one no | `EconomyFeature.Mail.cs:93` uses `>` deliberately: vmangos refuses only when `mailsCount > 100` (`MailHandler.cpp:259`), pinned by the existing `Recipient_box_is_refused_only_above_the_cap` and documented in `economy-fidelity.md`. The count is read before a separate commit (`:91` then `EfEconomyStore` `InsertMail`), so concurrent senders (one per sender, the sender is frozen after the first) can overfill. vmangos is single threaded so it has no such race | Fixed the race: `InsertMail` carries `RecipientCap`; the commit re-counts inside its transaction and refuses (same `>` meaning). The off by one is kept (fidelity). Test `InsertMail_WithRecipientCap_RechecksTheBoxInsideTheCommit`. A refusal at commit time answers INTERNAL_ERROR, not RECIPIENT_CAP_REACHED, because the commit result carries no reason |
| 4 Imported chair slot count stalls the world thread | Medium | Yes | `GameObjectChairs.cs:37` loops `data0` (uint) trigonometric iterations; reached from one CMSG_GAMEOBJ_USE via `GameObjectMapSystem.cs:573` | Fixed: `ClosestSlot(..., maxSlots = DefaultMaxSlots = 64)`, `0` = vmangos. Tests `ClosestSlot_ClampsAnImportedHugeSlotCount_...`, `ClosestSlot_ZeroMaxSlots_KeepsTheUnclampedVmangosLoop` |
| 5 Staff ban listings unbounded | Low | Yes (output), partly (queries) | `BanCommands.cs` `ReplyHistoryAsync`, `BanListAccount`, `BanListCharacter`, `BanListIp` print every row; `BanListCharacter` runs one history query per matching account. Needs Moderator rank. The full identity list read is the same in-memory directory the world already holds | Fixed output and the per-account query loop: `Bans:MaxListedEntries` (default 200, `0` = retail). Test `BanList_StopsAtMaxListedEntries_AndSaysSo`. Store-level paging and command rate limits not done (staff only, low) |

## Areas reviewed by hand beyond the scan

Sampled, not exhaustively audited: `WorldSession` auth path (ban checks fail closed, key not used until the second ban re-check
passes), `ChatSanitizer` (linear, byte-length bounded), `db-upgrade` SQL (identifiers come from the EF model through
`DelimitIdentifier`, `VACUUM INTO` is parameterised). No further findings.

## Config summary

| Setting | Default | Restores vmangos |
|---|---|---|
| `LootGenerator.MaxProcessCallsPerRoll` (code property) | 4096 | `0` |
| `GameObjectChairs.ClosestSlot` `maxSlots` (code parameter) | 64 | `0` |
| `Bans:MaxListedEntries` | 200 | `0` |
| Petition signature cap and in-commit mailbox recheck | on | none (they only act on malformed data / races) |