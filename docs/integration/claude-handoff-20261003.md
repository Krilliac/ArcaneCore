# Claude handoff: ArcaneCore, 2026-10-03

Nathan asked to finish this wave and move continuation to Claude. Start with the
published candidate [draft PR #11](https://github.com/Krilliac/ArcaneCore/pull/11),
branch `codex/integrate-feature-fleet-20261003`. All 19 original sources and all
10 later external sources are incorporated; none is excluded. Three historical
source objects are incorporated through their recorded squash commits. Read
[the exact source ledger](takeover-20261003.md), [machine accounting](takeover-accounting-20261003.json)
and [the original fleet record](fleet-20261003.md).

The final code merge checkpoint is `620cab672366a8c80d47b9e3a3a12ba8e3b86266`.
The published tip adds the corrected real-creature instance-loot test and this
handoff. Use the PR head and its exact-head checks as the current tip; do not
substitute a source branch's green checks for combined validation.

## Custody and environment

Local checkout: `C:\Users\Nathan\Documents\Codex\2026-10-02\task-2\ArcaneCore-continuation-20261003`,
work branch `codex/grok-salvage-20261003`. It is published to the canonical
candidate branch by ordinary fast-forward push. Existing original checkouts,
worktrees and partial feature histories were preserved. All Codex workers are
finished; there is no new gameplay wave or intentional background native build
after closeout. Recheck processes and CI before taking ownership.

Protected/default branch `claude/arcanecore-charter-rqadgw` stays at
`ac1204e00dd730ea04c081e0def0650879fd4492`; external Claude integration base
`claude/friendly-hamilton-cuz4j4` stays at
`0b5dcd0260d5919f2811e32f42f9d0ab720843c1`. Neither was merged, reset or rewritten.
No release/deployment/default merge was performed. Keep #11 draft until gates
and client acceptance justify a separate decision. Never force-push.

The linked original Claude session was inaccessible read-only:
`https://claude.ai/code/session_016eMgZECkSiMH9b9kBQRBtA`. Its private conversation
was not recovered or operated. Do not assume additional context from that URL.

Read the charter, current README, `docs/integration/seams.md`, global
`C:\Users\Nathan\.codex\AGENTS.md`, and any current Claude/local instructions.
The machine is shared with Sonder/Spark. .NET SDK 10.0.401 and existing EF/Pomelo
9 dependencies are installed; no paid infrastructure/new security access was
introduced. Serialize heavy native work. Preflight:

```powershell
powershell -NoProfile -File C:\Users\Nathan\.claude\scripts\fleet-preflight.ps1
git status --short
git rev-parse HEAD
git log -12 --format='%H %P %s'
gh pr view 11 --repo Krilliac/ArcaneCore --json headRefOid,isDraft,statusCheckRollup
```

Use a new isolated worktree for subsequent changes. Preserve unrelated edits,
stage specific paths, sign commits, and use bounded worker ownership/time limits.
No ExecutionPolicy bypass or speculative service restart is needed.

## Implemented in this continuation

- Separate reputation eligibility refuses Unfriendly NPC interaction without
  changing friendly combat reaction semantics.
- Repair snapshots persist money and final durability together.
- Instance unload retries survive in-flight worldport ACKs.
- Held spell aura/channel state stays frozen; weapon effects honor target masks.
- AI turns in melee reach, validates delayed assistance and contested PvP flags.
- Chest grid reload preserves partial contents and fresh object ownership;
  held eligible gold recipients retain their original shares; unknown nonzero
  locks refuse use; stale gameobject references cannot mutate replacements.
- GO/loot and creature systems route by exact `Map`; unload detaches NPC,
  progression, reputation and default-combat event roots. Same spawn GUIDs in
  different instances have independent corpse loot.
- Reward capabilities inspect actual effect/aura handlers and nested triggers.
  Permanent LearnSpell/CreateItem, teleport/summon without prerequisite contracts,
  and permanent/passive/unsupported aura grants refuse preparation.
- Login and deletion drain registered settlement barriers. Durable money
  publication and fresh wallet loading refresh accepted cash quests.
- Seller deletion conditionally matches all 12 scanned auction fields before
  deleting escrow. Unknown auctions retain reservations through fresh recovery;
  deletion invalidates older recovery callbacks.

- Integration of handoff items 1-6 (branch `claude/ac-integration`; **local verification only;
  exact-head hosted CI pending**):
  - Deletion outcome recovery: a durable `character_deletion` ledger, read-back after a thrown
    delete, a character-list sweep that finalizes pending deletions, an explicit-id create fence,
    and conditional (lifetime-fenced) post-delete removals that the hooks await.
  - Reputation write durability: failed writes are retained, never dropped, and carried by the next
    change, the login barrier, logout and shutdown.
  - Forward index repair: upgrades create the indexes of the tables they create; inline repair steps
    add the missing ones; startup is idempotent, resumable and serialized per component.
  - Durable consumed loot for dungeon-instance chests (`loot_state*` tables).
  - Reward collaborators: permanent `LearnSpell`/`CreateItem` and quest reputation rewards settle in
    the quest reward transaction, with destination and summon-owner preflight.
  - Economy recovery contract: a transactional auction snapshot, per-ID quarantine and backoff,
    allocator reseed after an external insert, and a finished `economy.md`.
  - Integration fix: the quest reward settlement now drains the reputation queue with
    `FlushCharacterAsync` (retry, refuse while not durable) rather than the pure barrier, so a
    retained older row cannot overwrite rows the reward writes; the reputation write queue's
    post-delete removal uses the conditional `DeleteDeletedCharacterAsync`.

Schema allocations at the first integration of 2026-10-03: **Auth 2 / World 10 / Characters 13**
(the vanilla-wave integration below takes them to **Auth 2 / World 14 / Characters 15**).
Characters: reputation 7, instances 8, spell state 9, economy 10, forward index repair 11
(inline step, `CharacterDbContext.IndexRepairVersion`), deletion outcome ledger 12
(`CharacterDeletionDataModule.Version`), durable loot state 13 (`LootStateDataModule.Version`).
World: GO/loot 7, AI 8, forward index repair 9 (inline step, `WorldDbContext.IndexRepairVersion`),
quest reputation reward columns 10 (`QuestReputationRewardWorldModule.Version`). Each number lives in
one constant and tests reference the constants or `Schema.CurrentVersion`. Before this integration the
allocation was Auth2 / World8 / Characters10. Original feature branches retain provisional numbers.
Never apply a source branch's provisional schema to a database already using the final allocation. New
changes require coordinated forward versions and complete cleanup registration.

## Evidence and its limits

Ten correction/source groups have exact source CI artifacts outside the checkout
in the parent task directory (`takeover-*-source-ci-evidence.json` and full
`grok-monitor-ci-<run>.log/.json`). Source counts differ with ancestry; never add
them. Combined economy `230be5a27cb235fda25f6b75f045ef00f9d62a5b` passed
[37114887000](https://github.com/Krilliac/ArcaneCore/actions/runs/37114887000):
9,191 tests, zero failures/skips/warnings/errors and 59 mock checks/142 frames.
Auction `697869a` passed 9,174. Seller-deletion `0323c03` passed 9,179 including
all four MariaDB/PostgreSQL bid-race cells and nine provider controls. Caller-owned
race cases explicitly use ReadCommitted; default-owned cases use provider defaults.

Behavioral original-source failures preceded fixes for reputation, repair,
instance transit, spell holds/masks, AI, loot, quest capabilities, login/cash
publication, auction recovery and map lifetime. Additional invalidation and
deletion barrier cases passed fixed real socket/EF tests. The instance-loot
fixture initially lacked an owning CreatureMapSystem; it was corrected to use
SpawnTemporary and real combat death, then passed.

Final native logs are `takeover-integrated-native-build-20261003.log`,
`takeover-integrated-native-tests-final-20261003.log`,
`takeover-integrated-native-mock-20261003.log` in the parent task directory.
The local provider race is deliberately skipped on SQLite; hosted MariaDB10.11
and PostgreSQL16 establish that proof. Prefix migrations exercise every
contiguous step and repeated startup, but do not establish populated released-v6
index parity, interrupted DDL recovery or actual MySQL server support.

Check the delivered exact-head CI and these logs before continuing. Useful
serialized commands after restore/build:

```powershell
dotnet build ArcaneCore.slnx -c Release -m:1 -p:UseSharedCompilation=false
dotnet test ArcaneCore.slnx -c Release --no-build -m:1 --verbosity normal
dotnet run --project tools/ArcaneCore.MockClient -c Release --no-build -- self-test
```

## Vanilla-fidelity wave (nine lanes; hosted CI green on `claude/vw-integration` at `40a2a9d`)

Integrated on `claude/vw-integration` from base `49448fd`, in the order stats, skills, death, spells, warrior,
creature AI, NPC/quests, content import, hot reload. Standing rule: retail 1.12.1 mechanics and data, references
vmangos (primary), mangos-classic, wow_messages, classic-db (read-only, nothing copied); every deviation is behind a
config option that defaults to retail. Each area document carries the file:line citations and its own limits; they
are not repeated here and remain the authority.

| Lane | Delivered | Where the exact limits are |
|---|---|---|
| stats-combat-formulas | Player stat system and formulas (base data tables, agility crit/dodge rates, spell crit, stamina/intellect bonuses, SetCanDualWield recompute, lenient mode with partial agility rows); no schema change beyond World 11 `PlayerStatsDataModule` | `docs/areas/stats.md` (Limits) |
| skills-professions | Skill model, gain rules, spell/proficiency handling, gathering (skinning orange roll at world max), trainer skill rules, persistence (Characters 14) | `docs/areas/skills.md` (Limits), `docs/integration/skills.md` |
| death-persistence | Death/ghost clock, life and ghost persistence (Characters 15), death seams and options, character hooks at the loaded phase | `docs/integration/death-persistence.md` (Limits) |
| spell-breadth-data-driven | `ISpellHandlerModule` discovery; Instakill, HealMaxHealth, Threat, DispelMechanic; stat/resistance/AP auras; visual auras; periodic leech auras; stack-count re-apply | `docs/areas/spells.md` ("Handler modules and spell breadth": remaining gaps listed by count) |
| warrior-mechanics | Combat spell data model and seams, warrior stances, next-swing spells (re-entrant cancel, out-of-range queueing), generic cast rules | `docs/areas/spells.md`, `docs/areas/combat.md`; forms other than 17-19 unhandled; talent tree not loaded |
| creature-ai-eventai | AI content model and importer fidelity, EventAI engine and combat-state events, aggro/relocation/initiate rules, combat leash (World 12) | `docs/areas/creature-ai.md` |
| npc-services-quests | GO interaction distance per type, conditions numbering (World 13), quest journal raid wiring, NPC service/quest fidelity | `docs/integration/npc-quest-fidelity.md` |
| content-import-full | Complete vanilla content import, RowMapper shared-state race fix, on-kill reputation importer (World 14) | `docs/areas/content-import.md` |
| hot-reload | Live reload of content tables: reload-all matches vmangos, retail empty-table and negative-number behaviour behind switches | `docs/areas/hot-reload.md` |

Schema after this wave (one named constant each; tests use constants and `Schema.CurrentVersion`):
**Auth 2 / World 14 / Characters 15**. Characters: skills 14, life 15. World: player stats 11, creature behaviour 12,
conditions 13, on-kill reputation 14. The lanes had allocated 14/14 (Characters) and 11/11/11/11 (World) in parallel;
they were renumbered in merge order. Remaining work from the list below is unchanged unless an area document above says
otherwise.

Known deviations that have no switch yet (accepted for this wave; each needs a retail-default switch as follow-up,
details in `docs/integration/npc-quest-fidelity.md`, "Known deviations without a switch"):

- Condition types that cannot be resolved fail closed (the row is "not satisfied" and its gossip option, vendor row or
  quest stays hidden) and cannot be configured.
- `AcceptableQuest` silently withholds quests that have a source spell, a source item/count, a PartyAccept, AutoRewarded
  or StayAlive flag, an exploration objective without a known area trigger, a reputation objective without a reputation
  owner, or an unsupported quest type, with no reply to the client.
- `Stats:RequireImportedData` defaults to false (vmangos refuses to start without the player base data); the startup log
  says so when the data is incomplete. It is the one deliberate difference of the stat feature.
- Hot reload is off by default (`HotReload:Commands=false`); a development server enables it.

Verification: local build, full suite and mock-client self-test, and hosted CI. The hosted `build-and-test` run 37140986207
(head `40a2a9d` of `claude/vw-integration`, with the MariaDB 10.11 and PostgreSQL 16 provider containers) concluded
success. Later commits on the branch (the review fixes) are covered by their own hosted runs, not by that one. This wave
has no real-client evidence: nothing here was tested against a 1.12.1 client, so do not read it as client-accepted.

## Wave 2 (twenty branches; local verification only; hosted CI pending)

Integrated on `claude/vw2-integration` on top of `claude/vw-integration` (wave 1, head `41babaf`), in this order: security hardening,
codex findings, inbound queue cap, game-logic security, code hot reload, GM commands, combat/CC/spell rules, rogue, druid,
hunter, casters, shaman/paladin, pets, talents, item mechanics, group loot/XP, instances/bosses, world state, pathfinding/collision,
ops/perf, then `claude/vw3-live-dev-runner` (the one-command dev runner, `arcane-mock live`, module hash allowlist; it came in last, once its worktree was committed and clean; the account tool keeps the codex-findings password source).
`claude/ci-fix-providers` is deliberately left to the coordinator. Same standing rule as wave 1: retail 1.12.1 behaviour by
default, deviations behind options that default to retail. **Nothing here has been run against a real 1.12.1 client, and
MariaDB/PostgreSQL provider tests only run on hosted CI: this wave is verified locally on SQLite only.**

### What each lane delivered, with its exact limits (the area documents remain the authority)

| Lane | Delivered | Limits (topics; details in the document) |
|---|---|---|
| security-hardening | Logon connection state, SRP degenerate verifier/salt, world auth enforces account status, stalled-writer teardown, logon limits and validation, connection admission, movement validation, channel cap (`World:Social:MaxJoinedChannels`, default 0 = retail unlimited) | `docs/security/hardening.md` "Not delivered": ban tables/IP bans and the wrong-password throttle, session-key age, char-screen idle kick, malformed-packet strikes, AddonInfo cap, `IPacketGate`/antiflood, chat hygiene, fuzz harness, reconnect commands |
| sec-codex-findings | WMO liquid grid overflow, `MySqlDumpReader` column-list spin and bounds, account tool password source, sweep fixes | `docs/security/codex-findings.md` |
| inbound-queue-cap | Inbound world packet queue cap, pre-auth deadline, cross-check tests | `docs/security/codex-net-auth.md` (banned live sessions are closed for new logins only) |
| sec-game-logic | `Economy:MailboxAccess` (Retail default, Permissive option), social write queue coalesce/bound/retain (`World:Social:WriteQueue:*`) | `docs/security/codex-game-logic.md` |
| code-hot-reload | `HotCodeGuard` launch gate, `CommandTableSource` live command table, `.hotcode`, module host `.hotmodule`, audit log | `docs/areas/code-hot-reload.md` "Not built": no hot replace of server code on a Release build, modules extend only opcode groups and chat command groups, no file watcher, no guaranteed unload |
| gm-commands | Vanilla GM command set with the retail security scale and texts, `World:GmCommands` | `docs/integration/gm-commands.md` "Not delivered": `.die/.revive/.setskill/.pet`, `.tele group/add/del`, most `.lookup` kinds, gm-spells/npc/gobject/quest/cheats, `.additemset`, speed and modify extras, bans/mutes/tickets (need schema), `.server plimit/corpses/log/exit`; GM state is not persisted |
| combat-cc-spell-rules | Hit/crit/resist rules, mechanics, crowd-control state, diminishing returns, immunities, dispel, pushback/lockout, absorb, caster-state gate | `docs/areas/spell-rules.md` "Limits": reflection/deflect, creature immunity data import, `spell_bonus`, melee call sites for `AbsorbDamage`, talents' `ISpellModifiers` implementation |
| class-rogue | Stealth/aura-interrupt dispatch, detection formula and updater, visibility rules, creature detection, group visibility mode | `docs/areas/rogue.md`: no form 30/slow, no Vanish/Preparation/Distract/Pick Pocket/poisons, no talent consumers, no energy modifiers, no proc-flag skip in the damage break, no creature stealth alert behaviour |
| class-druid | Pure form tables, feral formulas, Furor, Rip, Frenzied Regeneration, form-effect 9033 rules, power-type switch and feral caps, high-liquid interrupts, taxi interlock | `docs/areas/druid.md`: **aura 36 (ModShapeshift) for druid forms is not wired** (the warrior `ShapeshiftService` leaves druid forms unhandled), no cat/bear abilities, no nature spells, no `Druid:*` options; the liquid probe is terrain liquid only |
| class-hunter | Ranged weapon/ammo cast checks, ammo trailer packets, range leeway, traps and spell objects, tracking auras, Feign Death, Hunter's Mark target rule | `docs/areas/hunter.md`: no Auto Shot/wand/Throw (needs the auto-repeat slot), `RangedAttackSpeedPct` neutral, no aspect/sting stacking, no hunter pets, Feign Death consumers pending |
| class-casters | Spell power/bonus module, power-cost auras, drain/leech auras, channel trigger target, five-second timer, Improved Drain Mana | `docs/areas/casters.md`: formula-only coefficients (no `spell_bonus` table), no soul shards/portals/rituals/blink, five-second-rule consumers pending, no Health Funnel |
| class-shaman-paladin | Implicit target selectors (41-47, 61), totem system (effects 74, 87-90, 110) with `totem_spell` data and importer, shared shock cooldown guard | `docs/areas/class-shaman-paladin.md`: no active (Searing) totems, no totem immunity, no weapon imbues, no seals/judgement/blessings/auras/bubbles, no consecration/resurrect effects |
| pets | Summon model, pets/guardians/mini pets/wild summons, pet AI, charm info and action bar, pet tables (`pet_levelstats`, `petcreateinfo_spell`) | `docs/integration/pets.md` "Limits": placement uses the primary candidate only, pet stats and name generation (`InitStatsForLevel`) pending, a pet is unsummoned when its owner leaves the map. Its totem part was removed (see decisions) |
| talents | Talent catalogue (DBC), point accounting, learning, respec with cost, disabled-rank spells, persistence, coverage report | `docs/areas/talents.md`: no talent effects (needs the spell-modifier engine, aura 107/108, and procs), no `.reset talents` commands, no pet interaction, no supersede packets |
| item-mechanics | Load/trade/durability fixes, misc handlers, timed items and area limits, `CreateItem` spells, ammo (the single implementation), item maintenance | `docs/areas/items.md`: permissive requirements until registered, no `CMSG_USE_ITEM`/charges, no item sets/enchants/random properties, no item loot containers, no gift wrap, no buyback |
| group-loot-xp | Looter selection, group reward range, group loot packets and roll types, durable chest loot adjustments | `docs/areas/group-loot-xp.md`: no master-give, the loot recipient is the killer's group (no tap list), the money split uses the 3D 74 yd rule, XP/quest credit still use their own range |
| instances-bosses | Enter limiter, bind credit and resolvers, saved-instance packets, instance lookups | `docs/areas/instances.md`: no encounter mask/doors/variables, no area-trigger requirements, group binds do not survive a restart, packets need real-client confirmation |
| world-state-exploration | Server-derived zones/areas, world states, game-time options, weather, exploration XP, explored-zones persistence | `docs/areas/world-state.md`: game events beyond the schedule maths, rest, zone-entry consumers owned elsewhere, no BG/taxi/capture points, WMO indoor flag bit unconfirmed |
| pathfinding-collision | Path contracts, vmangos-compatible navmesh query rules, fallback seam, flier paths | `docs/areas/collision-pathing.md`: no swim/smooth paths, no raycast/random points, no BV tree/off-mesh links, no model-aware height, no WMO liquids, no transports |
| ops-perf | `check-config`, fail-fast validation, `.server shutdown/restart/idle*` with exit codes, `HostOptions` shutdown timeout, slow-update logging | `docs/areas/ops-perf.md`: no remote console/metrics/health endpoint, no login queue, no staggered autosave, no perf baselines; the restart exit code was not run end to end through a real host stop |

### Duplicate primitives resolved at integration (one implementation each)

- **Totems**: the shaman lane's `TotemSystem` owns effects 74/87-90/110; the pets lane's totem code and tests were removed. `Creature.IsTotem` asks `TotemQuery`.
- **Ammunition**: the item-mechanics lane (`PlayerInventory`, `character_item_state`, CMSG_SET_AMMO, starting ammo); the hunter lane's `character_ammo` module, feature, handler and persistence were removed; `PlayerAmmo` is a facade; ammo and wear consumption go through `PlayerInventory.ConsumeRangedAmmo`.
- **Dispel**: the combat/CC lane's; the casters lane's dispel partial and packets were removed (its tests run against the merged code, resist chance through `ISpellModifiers`).
- **Drain/leech auras (53, 64)**: registered once by the built-in `LeechAuras` module, which delegates to the casters lane's `DrainAuras`; the spell-breadth ticks were removed.
- **Server shutdown**: the ops lane's (`ServerLifecycleFeature`, exit codes); the GM lane's scheduler, feature and commands were removed, `.server set motd` stays.
- **Aura-interrupt helpers**: the rogue lane's `AuraInterruptMask` and `SpellSystem.RemoveAurasWithInterruptFlags`; the druid constants and extension were removed. The rogue swing event is `MeleeSwingFinished` (the warrior's `MeleeSwingResolved` is unchanged).
- **Spell modifier operations**: `Spells.SpellModOp` (spell-breadth) is the only enum; the combat lane's subset enum was removed. `ISpellModifiers` (combat) and `ISpellValueModifier` (spell-breadth) are still two seams and **nothing implements aura 107/108 yet** (talent effects stay inert).
- **Command table**: only the code-hot-reload lane introduced a live source (`CommandTableSource`); the wave-1 `.reload` command is an ordinary command in that table. GM `ChatCommands.CreateTable(configuration)` feeds it.
- **Combo points, shapeshift forms, area auras**: no second implementation exists. Combo points are the wave-1 `ComboPointService`; druid forms are not wired (see limits); the shaman lane leaves area auras to spell-breadth.
- **Configuration namespaces**: kept as each lane defined them except `Social`: the hardening and game-logic lanes used a bare `Social` section while wave 1 had `World:Social`; their keys moved under it (`World:Social:MaxJoinedChannels`, `World:Social:WriteQueue:*`) so one section is bound and reload-classified. No other collisions.

### Schema allocation after wave 2

One named constant each; tests reference the constants and `Schema.CurrentVersion`. **Auth 2 / World 17 / Characters 18.**
Characters: skills 14, life 15, item state 16 (`CharacterItemStateDataModule`, table `character_item_state`), talents 17
(`CharacterTalentDataModule`, `character_talent`, `character_spell_disabled`), explored zones 18 (`ExploredZonesDataModule`).
World: totems 15 (`TotemWorldDataModule`, `totem_spell`), pets 16 (`PetWorldDataModule`, `pet_levelstats`, `petcreateinfo_spell`),
world state 17 (`WorldStateDataModule`). The lanes built these as Characters 14/14/14/14 and World 11/11/11/11 in parallel and
were renumbered contiguously; the hunter lane's `character_ammo` module no longer exists. Every Characters module implements
`ICharacterDataCleanup`. The new stores (item state, talents, explored zones, totems, pets, world state) each have
`Providers()` theories, but those have only run on SQLite here.

### Not done / unverified (wave 2)

Everything in the limits column above, plus: druid forms (aura 36) and the cat/bear kit; the spell-modifier engine behind talents
and `ISpellModifiers`; auto-repeat shots; ranged haste wiring (`RangedAttackSpeedPct`); `spell_bonus` data for casters; creature
immunity data; items' `CMSG_USE_ITEM`; GM commands that need other lanes; group loot master-give and tap lists. Local verification
only; hosted CI pending, including every MariaDB/PostgreSQL provider theory. The live dev runner has no real-client or
hosted-CI evidence either.

## Remaining work, in priority order

Items 1-6 were delivered by the 2026-10-03 integration (`claude/ac-integration`). Their hosted CI is
the run of the wave-1 branch built on top of them (`claude/vw-integration` at `40a2a9d`, run 37140986207, success); a
source branch's own result is still not combined proof, and none of it involved a real client.

1. **Deletion outcome recovery. Delivered; exact limits remain.** Details: `character-delete.md`.
   `CHAR_DELETE_SUCCESS` means the rows are durably gone; an ambiguous outcome answers failure and is
   finalized by a sweep run when the owning account requests its character list, or by a retry. Limits:
   no background sweep (after a restart a pending row mainly blocks explicit-id recreation until that
   account enumerates or deletes); a finalizer that keeps failing is retried at most once per session;
   a commit a server finishes after its connection failed is not seen at that request; generated ids
   are not fenced (reuse was not established, and the conditional removals are defense in depth,
   not observed client loss); economy re-sends `SMSG_RECEIVED_MAIL` for recently returned letters on a
   re-run; the lost acknowledgement is injected with an EF interceptor, not a real network fault;
   MariaDB/PostgreSQL proof of the conditional deletes, unique index and ledger test comes only from
   hosted CI; real-client deletion acceptance is deferred; no soft delete or audit log.
2. **Reputation failure durability. Delivered; exact limits remain.** Details: `reputation.md`.
   Retention is in process only: a crash, or storage still down at graceful shutdown, loses the
   retained gain (shutdown fails loudly naming the characters; a crash cannot). There is no
   periodic background retry (an online player is retried at the next change, logout, relog or
   shutdown). A character with an unrecovered write cannot log in until storage recovers. Merging
   assumes absolute rows and a last-wins store, so a future delta-style write must not reuse the
   path. Deletion is never blocked by retained writes; a later shutdown failure can name a character
   whose deletion already removed its rows.
3. **Forward index repair and upgrade parity. Delivered; exact limits remain.** Details:
   `schema-index-repair.md`. Limits: MariaDB and PostgreSQL lock SQL, catalog queries and
   non-transactional DDL resume are proved only by hosted CI (the differ output is proved offline);
   actual MySQL 8 server support is unqualified; the frozen populated baseline is SQLite only; repair
   is explicit, there is no automatic drift detection for an index dropped later; realm/content
   seeding after bootstrap is outside the lock; fresh create on PostgreSQL is resumable rather than
   one transaction; older binaries fail closed on the version-0 marker. Duplicate guild-member or
   auction rows stop the upgrade and are never deleted (operator guide in the doc).
4. **Durable consumed loot. Partially delivered.** Delivered: dungeon-instance chest contents
   stored with awards, tied to the logical instance save across unload, recreation and restart,
   cleared only on reset or deletion; ordinary shared chests keep working; held group gold shares
   preserved. **Not delivered: item containers** (`CMSG_OPEN_ITEM` on a lootable item still answers
   cannot-loot; vmangos behaviour for generated container loot was not verified). Temporary and
   runtime chests in instances stay unsupported and chest gold is not generated or stored; a lost
   queued `InstanceSaved` write makes that instance's chests refuse until restart; the remainder of
   a money split is dropped; contents persistence is this port's choice, not verified against
   vmangos; MariaDB/PostgreSQL only through hosted CI; no real world-dump chest test. See
   `gameobjects-loot.md`.
5. **Complete reward collaborators. Delivered; exact limits remain.** Permanent spell, item and
   reputation grants settle atomically in the reward transaction with destination and
   summon-owner preflight, and the area-aura family shares the finite/nonpassive guard. Limits:
   summon rewards stay refused in the daemon (no `ISpellSummonSink`), covered with fakes only;
   transient teleport/summon rewards can still fail after the commit and are logged and lost,
   never replayed; the area-aura and non-base teleport/summon holes were latent (nothing in `src`
   registers those handlers); SQLite only until hosted CI; the packet order and several vmangos
   behaviours were applied from recollection, not re-checked, and have no real-client capture;
   deliberate difference: a `CreateItem` reward that does not fit refuses the turn-in. See
   `quest-progression.md` and `quest-settlement-async.md`.
6. **Economy recovery contract depth. Delivered; exact limits remain.** `economy.md` is finished.
   Limits: single writer (an external online insert of a new auction is invisible until restart and
   an external edit converges only when an operation on it is refused); mailboxes can be torn
   (separate reads) and mail has no reserved-id recovery; expiry backoff is in memory; a poisoned
   expired auction stays locked until an operator repairs the cause; the snapshot is proved on SQLite
   locally and on MariaDB/PostgreSQL only by hosted CI (real MySQL unqualified); no bidder index and
   the ledger is never pruned; fidelity to a real 1.12.1 client is unproved.
7. **Spell acceptance/depth.** Category-only cooldown client UI remains unproved;
   death/logout/relog aura persistence needs broader coverage. Finish unsupported
   effects/targets and real two-player aura attribution/UI acceptance.
8. **Content and bounded gameplay completion.** Finish the unified content importer
   CLI and supported dump mappings; import user-supplied full world data, DBC and
   terrain/collision/navmesh assets without committing proprietary/GPL data.
   Follow each area document for profession locks, NPC metadata/taxi/trainer/
   vendor/bank/spirit healer flows, quest categories/objectives, creature EventAI,
   pathfinding/fallback and instance reset/boss/respawn-state limitations. Missing
   extracted collision data currently permits documented fallback behavior; green
   synthetic CI does not validate real maps. Review M7-M14 acceptance documents
   and `docs/ROADMAP.md` before marking a whole milestone complete.
9. **Real-client acceptance**, detailed below. Fix acceptance failures before
   dependent gameplay expansion.
10. **M15 clustering**, still planned: ownership/session seams and durable fences
    (M15.0), gateway/one worker and versioned gRPC (15.1), realm identity/social
    and distinct instances (15.2), fenced handoff/static placement (15.3), host
    supervision/recovery/drain/capacity placement (15.4), compatible tooling,
    content rollout/migration/cross-host acceptance (15.5). Start from
    `docs/CLUSTERING_DESIGN.md`; runtime replacement/deployment require their
    own gates. Battlegrounds, honor, LFG/Warden are outside current delivered scope.

## Client/session context

Nathan deferred computer use. This wave did not launch/control a real client,
acceptance server, alter realmlist, or inspect client/run directories. The supplied
baseline report belongs to his selected file-access session, at server pin
`f8ae6e8b5f805e94f145194a82f80e876fa2dec3`, build5875. It reports realm/auth,
Human male Warrior creation, Northshire entry, 80.5 seconds connected and basic
movement. Physical Escape stopped it before normal 20-second logout, fresh login,
saved-position restoration and restart/relog. Those checks remain pending.

The separate quest UI handoff remains pinned to
`248accc71acbe70144b92c33761d8f9ae0ab07cc`; publishing did not switch that selected
session. Continue only when Nathan resumes the intended client session. Test
NPC greeting/accept/authoritative combat/reward UI, durable reward relog/restart,
two-player visibility/aura ownership, terrain/collision, services, economy and
instance entry/reset behavior. Keep the old report's pin distinct from this
integration tip. Automated socket/SQLite evidence does not make the project
client-accepted or generally playable.
