# Wave 3 integration (claude/vw4-integration)

Ten verified wave-3 lane branches merged one at a time (`git merge --no-ff`) onto `origin/main` at
`3bda712`. Lane reports (heads, deviations, open questions) come from the wave-3 workflow output; each
lane keeps its own detail doc under `docs/integration/` (`duels.md`, `economy-fidelity.md`, ...).

## What merged

| Lane | Branch | Lane head | Merged |
|---|---|---|---|
| duels | claude/vw4-duels | 46e863d | yes |
| movement-environment | claude/vw4-movement-environment | e73e8db | yes |
| gameobject-types | claude/vw4-gameobject-types | 3581629 | yes |
| fishing-special-loot | claude/vw4-fishing-special-loot | e23686f | yes |
| economy-fidelity | claude/vw4-economy-fidelity | 8124924 | yes |
| social-guild-petitions | claude/vw4-social-guild-petitions | 58e3e5b | yes |
| chat-languages-channels | claude/vw4-chat-languages-channels | c1c11bc | yes |
| character-creation-rules | claude/vw4-character-creation-rules | b6689ab | yes |
| live-ban-enforcement | claude/vw4-live-ban-enforcement | 9d11d6d | yes (its base `vw3-inbound-queue-cap` was already in main) |
| db-upgrade-tooling | claude/vw4-db-upgrade-tooling | c76dd3d | yes |

## Schema renumbering

Main already held World 2-17 (9 is the inline index repair) and Characters 2-18 (11 is the inline
index repair). Auth was at 2. Every lane that added a module picked "next free" from the old base, so
four collided. Renumbered after main's highest, in merge order. Each lane keeps its number in one
`Version` constant; `IntegratedSchemaTests` reads the constants, so only the constants changed.

| Module | Database | Lane number | Integrated number |
|---|---|---|---|
| GameObjectSpawnDataModule (gameobject-types) | World | 15 | 18 |
| SpecialLootDataModule (fishing-special-loot) | World | 15 | 19 |
| StartActionWorldModule (character-creation-rules) | World | 15 | 20 |
| ItemLootDataModule (fishing-special-loot) | Characters | 16 | 19 |
| PetitionDataModule (social-guild-petitions) | Characters | 16 | 20 |
| BanDataModule (live-ban-enforcement) | Auth | 3 | 3 (main was at 2, no collision) |

Final tips: World 20, Characters 20, Auth 3. Versions are contiguous and unique per database
(`DataModules.Compose` throws on gaps and `IntegratedSchemaTests` asserts it).
`ItemLootDataModule` and `PetitionDataModule` implement `ICharacterDataCleanup`; the World modules
hold no per-character rows. The lane-reported "no schema change" lines in the workflow output refer to
the lane's final fix commit, not the lane as a whole.

## Conflicts and how they were resolved

- **duels / MapCombat.Melee.cs**: main's `combatLink` gate (hunter trap fix) and the lane's duel 1 hp
  clamp touched the same line. Kept both: `ApplyDuelClamp` first, then `if (combatLink)`.
- **movement-environment / MovementHandlers.cs**: main's `MovementValidator` (invalid packets dropped,
  `World:StrictMovementFiniteness`) versus the lane's `ApplyObserved`/`EnsureFinite` (throw, kick).
  Result: validator early return, then `ApplyObserved` (the lane asked for exactly this order).
  `EnsureFinite` became `MovementHandlers.IsAcceptable(session, movement)` returning false for a
  dropped packet, used by the root/flag/speed/knockback ack handlers, so acks are also dropped, not
  kicked, and honour the strict-finiteness switch. Main's `HandleRootAck` is superseded by the lane's
  `Locomotion/RootAckHandler` (pending-change ledger).
- **gameobject-types, fishing-special-loot, social, character-creation / IntegratedSchemaTests**: table
  rows from both sides kept; see the numbering table.
- **fishing-special-loot / LootService.cs**: main's `EnsureMasterLooterAvailable` + `ApplyLooterPlan`
  (round-robin plan) replaced the lane's `AssignOwner`; the lane's `CloseReplacedBag` is kept after it.
- **fishing-special-loot / ItemPersistence.cs**: main's selected-ammo state and the lane's container-loot
  staging both run in the same `SaveChanges`.
- **economy-fidelity / EconomyAccess.cs + MailboxAccessTests.cs + EconomyLifecycleTests.cs**: both sides
  fixed the any-GUID mailbox. Main's `GameObjectMailboxAccess` / `PermissiveMailboxAccess` with
  `Economy:MailboxAccess` was kept; the lane's `DefaultMailboxAccess` was dropped (nothing else used it).
- **economy-fidelity / EconomyFeature.Trade.cs**: main's `CanBeTraded` and `trade: true` staging kept; the
  lane's early slot-range cancel and the trade-space notifications (`Economy:TradeSpaceNotifications`) kept.
- **social-guild-petitions / SocialWriteQueue.cs**: main rewrote the queue (per-key coalescing, retained
  failures, bounds). The lane's petition writes were re-expressed in that model: new keys `Petition`,
  `PetitionComplete` (guild snapshot + petition delete in one store call) and `PetitionPurge` (queued
  right after the social purge of a deleted character). The queue implements both `ISocialPersistence`
  and `IPetitionPersistence`. Petition writes therefore also coalesce, retry and are retained.
- **social-guild-petitions / SocialFeature.cs**: binds `World:Guild` and `World:Social:WriteQueue`.
- **chat-languages-channels / ChatHandlers.cs, WorldConfigKeys.cs, docs/areas/social.md**: usings,
  live keys (`MaxJoinedChannels` and `VmangosChannelExtensions`) and the Options paragraph combined.
- **Cross-lane duplicate (found by tests, not by git): chat mute and anti-flood.** The social lane
  (`ChatRestrictionFeature` + `ChatRestrictionService`, an `IChatMessageHandler`) and the chat lane
  (`ChatFeature` + `IChatMuteSource`, gating inside `ChatHandlers`) each implemented the vmangos mute check,
  flood counter and mute-only-whisper-staff rule, both bound to `World:Chat`. Merged as is they counted and
  muted twice and ignored each other's runtime options (`ChatGateTests.FloodControl_...` and
  `ChatRestrictionEndToEndTests.AMutedSpeaker_...` failed). Resolution: `ChatFeature` is the single gate;
  `ChatRestrictionFeature` is now an `IChatMuteSource` over the service's explicit mute table (the `.mute` /
  stored-mute seam) and no longer an `IChatMessageHandler`; `IChatMuteSource` is a registered seam interface.
  The service's pure flood logic and its unit tests remain but the runtime no longer calls them (dead code to
  prune later). `ChatRestrictionEndToEndTests`: the handler-ordering test was removed (no longer a handler) and
  the staff member now enables `.whispers on`, because the chat lane implements retail `AcceptsWhispersFrom`
  (a plain player cannot whisper a staff member who does not accept whispers).
- **Retail GM text conventions (main) vs lane test expectations**: `BanCommandTests` expected "Incorrect syntax."
  and "There is no such command"; main's GM table prints the command's own `Syntax: ...` help in place of
  "Incorrect syntax." and answers "This command is not available to you." for a command above the invoker's
  level. Tests updated to main's texts; the ban commands themselves are unchanged.
- **StartActionsWorldTests**: asserted `PlayedTime == 0` after create+login; played time is now persisted from the
  wall clock, so it read 1 on a slow run. The test pins level and money only.
- **live-ban-enforcement / WorldServiceCollectionExtensions.cs, WorldTestHost.cs, AccountTool**: all
  option bindings kept; `WorldTestHost.Start` takes both `configureServices` and `banOptions`; the
  account tool keeps main's no-echo credential reader and the lane's ban/unban/baninfo/banlist commands.
- **db-upgrade-tooling / World Program.cs, AccountTool usage**: hot-code banner, `DatabaseStartup`
  (policy-aware schema init) and `ExitCodes.Current` all kept.

Command-group roots: no two groups register the same root (the table builder throws at startup on a
duplicate and the World command tests build the real table).

## Deviations (all behind config, default retail)

Collected from the lane reports; none are on by default except where noted.

- `Locomotion:SlimeDamage` (default false): hurts in slime like lava.
- Duel boundary default 50/40 yd (mangos-classic); vmangos 75/70 is an option (`World:Duel`).
- `Economy:ReturnExpiredMoneyOnlyMail=false`, `AllowDeleteWithAttachments=true`: opt-in vmangos behaviours with no retail source (defaults are the pre-lane behaviour).
- `World:Guild:AllowClientGuildCreate` (default true, as vmangos), `World:Guild:DeleteRankMovesMembers` (default false, vmangos).
- Flood mute is in memory only; link-check level 3 behaves as 2.
- Moderator/GameMaster/Administrator are a four-level compression of vmangos's 0-7 gm scale.
- Movement: packed GUIDs for the new SMSG_MOVE_* packets and a root ack without the Root flag is applied, not kicked (unverified against a real client).
- Integration choice: movement acks with invalid movement blocks are now dropped rather than kicking the client (follows main's wave-2 retail-validation policy).

## Open questions

- Real 1.12.1 client captures: root-family GUID packing, 50/40 yd duel boundary, slime damage, knockback vertical speed rounding, lava tick values, environmental PvP death.
- Who casts Ghost aura 8326; who owns server-driven player splines; WMO liquid / LiquidType remaps; taxi mount-aura removal.
- `IEnvironmentalDamageMitigation` / `IFallDamageModifiers` need real registrations when the spell combat-rules lane lands.
- `PickpocketSpells` / `DisenchantSpells` throw on a second registration of effects 71/99, and TRANS_DOOR handlers must chain via `GetEffectHandler`: later lanes must respect attach order.
- Retail source for expired money-only mail and delete-with-attachment behaviour.
- `GhostPersistenceTests.LogoutWhileDead_RelogsAsAGhostAtTheBody` was reported intermittently red by the economy lane (death lane owns it).
- `TeamForRace` for an unknown race (vmangos returns ALLIANCE) was not aligned.
- Whether a stricter all-level-3 `Bans:*` tier is wanted.
- Hosted-only real-server test for applying a Missing upgrade plan with `RefuseActiveSessions`.
- Provider (MariaDB/PostgreSQL) schema and store tests run only on hosted CI; the renumbering above is verified locally on SQLite only.
