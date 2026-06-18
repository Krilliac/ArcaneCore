# ArcaneCore — Build Charter

> Project root document. This is the binding charter for ArcaneCore development.

---

## §0 — What this project IS and IS NOT

ArcaneCore is a **net-new, from-scratch** WoW **1.12.1 (client build 5875)**
server emulator in **C# / .NET 9**.

**There is no prior canonical codebase.** Any "ArcaneCore" zips, source dumps, or
reconstructions that predate this charter are **void**. Do **not** import, read,
or "continue" them. They were generated from a spec, never compiled, never run,
and contain invented signatures and at least one known protocol error (see §3).
Starting from them would cost more in auditing fiction than writing real code.

What is real and may be referenced for *lineage and ideas only*: the developer's
prior **MangosSharp** work (clustering restoration, ported bug fixes). Even that
is not copied in — it informs design, it is not a source.

---

## §1 — Prime Directives (non-negotiable, override anything below)

1. **Ground truth over generation.** Every protocol detail (opcode value, struct
   layout, field order, hash construction) must be verified against a real
   reference (§4) before it is written. In code comments, cite *which* reference
   confirmed it. If you cannot verify it, **stop and ask** — do not guess.

2. **Incremental and gated.** Build exactly one milestone at a time (§6). Do
   **not** scaffold, stub, or pre-create later milestones' projects/files.
   Do not advance to the next milestone without the developer's explicit "go".

3. **Verified against a real client.** A milestone is "done" only when its
   acceptance test passes against an **actual 1.12.1 (5875) client** — not when
   the code compiles, not when a unit test you wrote passes.

4. **Complete within scope, absent outside it.** Inside the current milestone:
   no TODOs, no placeholders, no stubs, no "implement later" — finish it for
   real. Outside the current milestone: the code simply **does not exist yet**.
   These are not in tension: completeness is scoped to the milestone, not the
   whole emulator.

5. **No invented API surface.** Never fabricate a method signature, packet field,
   opcode number, or DBC column. If a detail is unknown, it is a blocker to be
   resolved by reading a reference or asking — never by plausible-looking
   invention.

---

## §2 — Tech baseline

- **Language/runtime:** C# 13 / .NET 9. `async`/`await`, `Span<T>`/`Memory<T>`
  for packet buffers, source generators where they earn their keep.
- **Target protocol:** WoW **1.12.1**, client build **5875**. Nothing newer.
  Opcodes, UpdateFields, and packet shapes are build-specific — values from
  later expansions are wrong and will silently desync or disconnect the client.
- **Database:** **MariaDB** is the primary engine, with **MySQL** and
  **PostgreSQL 16** also supported via a provider abstraction (EF Core).
- **Style:** match the `.editorconfig` at the repo root. Nullable enabled,
  warnings-as-errors in CI.

---

## §3 — Known-correct anchors (use these; the old reconstruction got these wrong)

These are starting orientation, **not** a substitute for verification — confirm
the exact byte-level computation against §4 before implementing.

- **Two daemons.** Logon/realm server on **TCP 3724**; world server on **TCP
  8085** (default). Separate processes.
- **Auth is SRP6 (WoW variant).** `g = 7`, `k = 3`, the well-known 32-byte WoW
  prime `N`. **Username and password are uppercased** before hashing. The
  session key `K` comes from `S` via the **SHA1-interleave** hash. `M1`/`M2`
  proofs use the standard WoW construction. Implement this by reading a real
  `AuthSocket`/`SRP6` (cmangos or vmangos) line-by-line — **do not** implement it
  from this paragraph alone.
- **Packet header encryption — CRITICAL CORRECTION.** Vanilla 1.12.1 uses the
  **rolling byte cipher seeded from the SRP6 session key**, applied to packet
  *headers* only, beginning after `CMSG_AUTH_SESSION`. It is **NOT ARC4**. ARC4
  header encryption is a 2.x+ (TBC onward) thing. The prior reconstruction used
  ARC4 here — that is the single most important bug not to reproduce.
- **Header sizes.** Server→client (SMSG) header = **4 bytes**: 2-byte size
  (big-endian) + 2-byte opcode (little-endian). Client→server (CMSG) header =
  **6 bytes**: 2-byte size (big-endian) + 4-byte opcode (little-endian).
- **UpdateFields / DBC** are build-5875-specific. Pull exact indices and DBC
  layouts from references; never carry over values from a later core.

---

## §4 — Reference repos (ground truth — reimplement, don't lift)

Read these for *behavior and exact values*. They are **GPL**; if ArcaneCore's
license differs, reimplement from observed behavior + specs rather than copying
code verbatim. Local clones live in `/home/user/refs` (outside this repo).

- **cmangos/mangos-classic** — closest maintained vanilla reference for 1.12.x.
- **vmangos/core** — vanilla-focused, actively maintained; excellent auth/world
  socket and protocol reference.
- **mangoszero/server** — additional vanilla cross-check.
- **wowdev.wiki** — canonical protocol/opcode/format specs (the neutral source).
- **gtker/wow_messages, gtker/wow_srp** — machine-readable packet/opcode/SRP6
  definitions and known-answer test vectors.
- **EmberEmu/Ember** — clean modern C++ vanilla auth daemon reference.
- **WCell** — C#/.NET architecture idioms; auto-create-on-login mechanism.
- **MangosSharp / mangosvb** — .NET lineage patterns.

When two references disagree, prefer the one the real client agrees with (§1.3),
and note the discrepancy in a comment.

---

## §5 — Architecture north star (build toward; do NOT build all of it now)

```
ArcaneCore.Kernel     plugin host, event bus, lifecycle, data seams
ArcaneCore.Protocol   opcodes, header read/write, header crypto, packet (de)serialization
ArcaneCore.Realm      logon daemon (SRP6, realm list)
ArcaneCore.World      world daemon (sessions, world socket, dispatch)
ArcaneCore.Game       entities, spells, combat, movement      ← later milestones
ArcaneCore.Data       DB access, DBC loading
```

**Plugin-first** and **gRPC clustering** are the design identity — but they are
*seams*, not day-one builds. Define the interface boundary where clustering will
later slot in (e.g. world↔world and realm↔world talk through an abstraction, not
direct calls), so it can be implemented later without a rewrite. Do **not**
implement gRPC transport until a single-process server actually works.

---

## §6 — Milestone plan (strictly gated)

For **each** milestone, the first deliverable is a written **acceptance test
defined against the real client**, approved before any code is written.

- **M1 — Logon + SRP6.** Realm server accepts `CMD_AUTH_LOGON_CHALLENGE` /
  `CMD_AUTH_LOGON_PROOF`, validates a real account, returns the realm list.
  **Gate / acceptance:** a real 1.12.1 (5875) client logs in and sees the realm
  at the realm-list screen. *Nothing proceeds past here until this passes.*
- **M2 — World handshake.** `SMSG_AUTH_CHALLENGE` → `CMSG_AUTH_SESSION`, session
  key validation, **header encryption engages** (the rolling cipher, §3).
  **Acceptance:** client clears the world handshake and reaches an (empty)
  character-select screen.
- **M3 — Character lifecycle.** Create / enumerate / delete / login.
  **Acceptance:** client creates a character and enters the world, standing
  still, no disconnect.
- **M4 — Movement + space.** Movement opcodes/heartbeats, map/grid/cell,
  visibility. **Acceptance:** character walks; two clients see each other move.
- **M5+ — Gameplay.** Spells, auras, combat, items, AI — each its own gated
  milestone with its own client-verified acceptance test.

---

## §7 — Database & assets the developer supplies

- A real **1.12.1 (5875) client** for testing.
- DBC files + extracted map data (from the 5875 client) when milestones need them
  (M3+).
- Confirmed DB engine (§2) and credentials for local dev.
- An empty `Krilliac/ArcaneCore` repo with CI enabled.

---

## §8 — Definition of done (every milestone)

1. Acceptance test (vs. real client) passed and demonstrated.
2. No stubs/TODOs/placeholders in shipped scope.
3. Every protocol detail carries a comment citing the reference that verified it.
4. CI green (build + warnings-as-errors + any unit tests).
5. A short `MILESTONE_Mx.md` noting what was verified, against which client build,
   and any reference discrepancies found.
