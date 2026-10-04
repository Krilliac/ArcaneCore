# Codex packet-layout audit: validation and disposition

Source audited: origin/main 7313b9e (read-only Codex static pass, `cx/out/protocol-audit`: report.md, findings.json, 463 of 497 usage-based opcode entries explicitly *unverified*). The audit is real and specific (opcode, our file:line, reference file:line for every finding), not an empty summary, but it is a narrow pass: 2 mismatches, 5 reference conflicts, 27 structural spot checks. Absence of a finding is not evidence of correctness for the 463 unverified entries.

Validation method: each claim re-read against our code at 7313b9e and against D:\refs\wow_messages (primary for 1.12), D:\refs\vmangos and D:\refs\mangos-classic. Fixes were proven RED first with exact-byte tests.

| ID | Opcode | Claim | Validation | Disposition |
| --- | --- | --- | --- | --- |
| PA-001 | SMSG_AUTH_RESPONSE (AUTH_OK) | Initial success reply was 1 byte; must be u8 result + u32 billing_time + u8 billing_flags + u32 billing_rested (10 bytes). | REAL. wowm smsg_auth_response.wowm AUTH_OK branch, vmangos World.cpp:324-333 and mangos-classic WorldSession::SendAuthOk agree. Ours: WorldSession.SendAuthResponse sent `[(byte)code]` for every code. A 1.12 client reading the billing fields would run off the packet. | FIXED (RED test: WorldHandshakeTests asserts the 10-byte body; failure replies stay 1 byte). |
| PA-002 | SMSG_SPELL_DELAYED | Wrote packed GUID + u32; references require full u64 GUID + u32 (12 bytes). | REAL. wowm smsg_spell_delayed.wowm `Guid guid; u32`; vmangos Packets/Spell.cpp:656-660 and mangos-classic Spell.cpp:6834-6836 stream an ObjectGuid whose operator<< is a raw u64 (ObjectGuid.cpp:174-177 / :74-77). Our comment claiming the servers use a packed GUID was wrong. | FIXED (RED test SpellRulePacketTests.SpellDelayed_IsAFullEightByteGuidThenU32Delay; stale note in spells-persistence.md corrected). |
| C-1 | SMSG_GROUP_LIST | wowm stops at loot threshold; vmangos adds a difficulty byte (post 1.10.2). | Reference conflict, not a bug: we follow vmangos (GroupPackets.cs:57-60). | No change; needs a 5875 client capture. |
| C-2 | SMSG_QUESTGIVER_OFFER_REWARD | Last words labelled differently (wowm) vs quest flags + reward spell (vmangos). | Same width and order; naming only. | No change. |
| C-3 | SMSG_LOOT_RESPONSE | wowm short vanilla item entry vs 22-byte entry in mangos-classic. | Conflict; we match mangos-classic. | No change; needs capture. |
| C-4 | CMD_REALM_LIST footer | wowm footer zero vs vmangos `u16 2` for 5875. | Conflict; we match vmangos (RealmListWriter.cs:34). | No change. |
| C-5 | SMSG_TRANSFER_ABORTED | wowm: map, reason, argument; vmangos Misc.cpp:952-955 and mangos-classic WorldSession.cpp:1189-1191 send only the reason byte. Re-verified by me. | Genuine conflict affecting length; ours matches the servers (TeleportPackets.cs:61). | Unfixed deliberately: changing bytes on a three-way disagreement without a client capture risks regressions. |

## Unfixed / follow-up

- 463 usage-based opcode entries were never layout-verified by this audit; update-field index tables and per-object-type update blocks were not compared at all. A second pass (ideally a per-opcode golden-byte test generated from wow_messages test vectors) is the real next step.
- C-1, C-3, C-5 need a real 1.12.1 client parse or packet capture to settle.