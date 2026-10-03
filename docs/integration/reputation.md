# Reputation and factions (fleet round 2, `feat/reputation`)

Branch `feat/reputation` from integration head `0d32fba1070a0be08932a85551a4c1b4ca191cfc`;
PR into `codex/integrate-feature-fleet-20261003`. Schema: **characters v7** (reserved for
reputation in the round-2 brief). World schema: unchanged.

## Provenance

Behavior is re-implemented (no GPL source copied) from:

- vmangos/core `4b3d241cffe245a1f68da11380bce96c23db48c0`: `Game/Objects/ReputationMgr.cpp`
  (Initialize, LoadFromDB/SaveToDB flag rules, SetOneFactionReputation, SetAtWar, SetInactive,
  SendState/SendInitialReputations/SendVisible, rank table), `Game/Objects/Object.cpp`
  (GetReactionTo / GetFactionReactionTo), `Game/Objects/Player.cpp` (CalculateReputationGain,
  RewardReputation for kills and quests, GetReputationPriceDiscount), `Game/Server/Packets/Misc.cpp`
  (CMSG_SET_FACTION_ATWAR / _INACTIVE / CMSG_SET_WATCHED_FACTION reads), `Formulas.h` (gray level).
- cmangos `DBCfmt.h` `FactionEntryfmt` for the build-5875 Faction.dbc layout (37 fields).
- gtker/wow_messages (MIT) `wowm/world/faction/*.wowm` for the server message layouts.

**Wire note (u16 vs u32).** gtker types the client's list index in CMSG_SET_FACTION_ATWAR /
CMSG_SET_FACTION_INACTIVE as a u16 `Faction`; vmangos and TrinityCore read a u32
reputation-list index followed by a u8. ArcaneCore follows vmangos: both CMSGs must be exactly
5 bytes (u32 slot < 64, u8) and anything else is ignored. CMSG_SET_WATCHED_FACTION is exactly an
i32 (-1 clears). Real-client capture remains the arbiter.

## What it does

| Area | Code |
| --- | --- |
| Faction.dbc catalog (ids, list slots 0..63, race/class base slots, parent) | `Kernel/Reputation/FactionCatalog.cs`, `Data/Reputation/FactionDbcReader.cs` |
| Rank table, clamp (-42000..42999), gray level, gain scaling, dither | `Game/Reputation/ReputationMath.cs` |
| Per-player state (ReputationMgr): defaults by race/class, visibility, war, inactive, watched, load rules, dirty tracking | `Game/Reputation/PlayerReputation.cs` |
| Packets: SMSG_INITIALIZE_FACTIONS (u32 64 + 64 × (u8 flags, i32 standing)), SMSG_SET_FACTION_STANDING (u32 count + (u32 slot, i32 standing)), SMSG_SET_FACTION_VISIBLE (u32), strict CMSG parsers | `Game/Reputation/ReputationPackets.cs` |
| Reactions CvP / PvC with fail-closed resolution | `Game/Reputation/ReputationReactions.cs` |
| World-thread owner: registry, notifications, kill and quest rewards, NPC seam (`IPlayerReputation`: rank, 10 % Honored discount) | `Game/Reputation/ReputationService.cs`, `ReputationContracts.cs` |
| Feature: config, load on login, ordered write queue, kill hook, opcode handlers | `World/Reputation/*.cs` |
| Storage (characters v7) | `Data/Reputation/CharacterReputationDataModule.cs` |

Rules worth knowing:

- Standings are stored and sent **relative to the race/class base**; effective reputation is
  base + standing, clamped to Hated..Exalted.
- Stored rows pass through the same rules as vmangos LoadFromDB, so the database cannot clear
  `PeaceForced`, make an `InvisibleForced`/`Hidden` faction visible, or keep a Hostile-or-worse
  faction out of war. Unknown or reputation-less factions in storage are ignored; an invalid
  watched slot loads as -1.
- Falling to Hostile declares war, except that `PeaceForced` blocks war until the faction is
  Hated. **Deliberate deviation:** vmangos compares the *relative* standing for that exception;
  ArcaneCore uses the effective reputation so a nonzero race base cannot open or close it.
- The client may toggle war (not while in combat, not for hidden/forced-invisible slots, not
  against forced peace above Hated), inactive (visible, non-hidden slots only) and the watched
  faction (-1 or a visible slot). Toggles are persisted, not echoed. The watched slot is
  published in `PLAYER_FIELD_WATCHED_FACTION_INDEX`.
- Changes send SMSG_SET_FACTION_VISIBLE once per newly visible slot, then one
  SMSG_SET_FACTION_STANDING carrying the changed slot first and every other pending slot.
- Kill rewards (vmangos `creature_onkill_reputation` shape): team-dependent rows give the
  Alliance faction 1 and the Horde faction 2; the `MaxStanding` rank caps further gain (patch
  1.9); team awards give half to the parent faction; gray kills use `RateLowLevelKill`.
- Quest rewards: quest level ≤ 0 uses the player's level; gains shrink 20 % per level once the
  quest is five or more levels below the player (floor 20 %); losses are never scaled.
- Reactions fail closed: a nonzero faction absent from Faction.dbc, or a reputation faction for
  a player whose state is not loaded, resolves to "unknown" (callers refuse interaction).

## Schema (characters v7)

New tables only; nothing existing changes.

| Table | Key | Columns |
| --- | --- | --- |
| `character_reputation` | (`guid`, `faction`) | `standing` int (relative to base), `flags` uint |
| `character_reputation_watch` | `guid` | `watched_faction` int (-1 = none) |

The watched faction lives in its own table rather than a new `characters` column so the
module stays self-contained. Writes are upserts (last write per faction wins), skip a missing
character, and run through a single ordered consumer (`ReputationWriteQueue`). Character creation
flushes the queue first and also clears rows left by a deleted character whose id was reused.
No schema change was needed for failure durability (below).

## Failure durability

A reputation change is never dropped on a storage failure. `ReputationWriteQueue` merges every
change (absolute faction rows, last value wins, plus the watched slot) into a per-character
in-memory overlay and writes the whole overlay, so a write that fails its 3 attempts (200/400 ms
backoff, fresh DI scope each attempt) is **retained** instead of lost, and the next write for that
character carries it. The overlay is cleared only for the rows that were actually written and not
changed since. Replaying a write is safe: every store call is an idempotent upsert or delete
(`Replay_IsIdempotent_OnEveryProvider` covers SQLite, MariaDB and PostgreSQL). Writes queued for a
character while one is already waiting are coalesced, so an outage does not grow the queue.

Retry triggers, all per character:

- the next reputation change for the character (it writes everything retained);
- login: `FlushCharacterAsync` runs after every earlier write, retries once more, and **refuses the
  login** (`CharLoginFailed`, the same fail-closed path as the quest and character-save queues)
  while the character is still unrecovered, so stale stored rows never become the live state;
- logout: a fire-and-forget retry is queued when a character with retained writes leaves;
- shutdown: `StopAsync` runs a final retry of every retained character and throws an
  `InvalidOperationException` naming the character ids if storage is still failing. That surfaces
  through `WorldFeatures.StopWorldFeaturesAsync` (docs/integration/seams.md). `StopAsync` is
  idempotent and returns the same failure to a second caller, so disposing the feature after
  stopping it reports the failure again (as the quest feature does).

`FlushAsync` stays a pure ordered barrier (character creation and deletion use it): it neither
retries nor throws, so one character's persistent failure cannot stall unrelated creations or
deletions. Deletion is never blocked by retained writes: `DeleteCharacter` discards whatever was
retained and queues the delete (a failed delete is itself retained and retried; a later shutdown
failure can therefore name a character whose deletion transaction already removed its rows).
Creation drops anything retained for a reused id after its flush.

Limits (honest):

- Retention is in process only. A crash, or storage still down at graceful shutdown, loses the
  retained gain; shutdown makes that loss loud, a crash cannot. No spill-to-disk journal exists.
- There is no periodic background retry: an online player with a retained failure and no further
  reputation change is retried only at logout, relog or shutdown.
- A character with an unrecovered write cannot log in until storage recovers (user-visible refusal).
- A change arriving after the queue stopped is only logged and held in memory; the host stops the
  world before its features, so this does not happen in the daemon.
- A host without an `ICharacterReputationStore` keeps the old tolerant no-op (the empty-catalog test
  host); the quest persistence fails closed there instead.
- Merging relies on rows being absolute values and the store being last-wins. A future delta-style
  write (quest reputation inside a settlement transaction) must not reuse this path unchanged.
- The retained-write retry on login and shutdown costs 3 attempts plus backoff (about 0.6 s) per
  affected character.

Proof: `ReputationWriteDurabilityTests`, `ReputationWriteRetentionTests` (queue over real EF/SQLite
with injected failures, simulated restart), `ReputationWorldTests` (socket: refused relog until
recovery then gain and watched faction restored, logout retry, deletion with a retained failure)
and `ReputationStoreTests.Replay_IsIdempotent_OnEveryProvider`.

## Configuration

Section `Reputation`:

| Key | Default | Meaning |
| --- | --- | --- |
| `FactionDbcPath` | unset | Path to build-5875 `Faction.dbc`. Unset (and no DI `FactionCatalog`) = empty catalog. |
| `RateGain` | 1.0 | vmangos `Rate.Reputation.Gain`. |
| `RateLowLevelKill` | 0.2 | vmangos `Rate.Reputation.LowLevel.Kill`. |

With an empty catalog, login still sends 64 empty slots, every reputation-faction NPC stays
unresolvable (as before this branch) and the quest/vendor/trainer reputation seam is not
installed (requirements fail closed).

## Shared-file edits

1. `src/ArcaneCore.Kernel/Npc/FactionTemplateCatalog.cs` — added `FactionTemplateRecord.IsFriendlyTo`
   and `IsContestedGuard`; the private contested-guard constant became public
   `FactionTemplateCatalog.ContestedGuardFlag`. Existing behavior unchanged.
2. `src/ArcaneCore.Game/Npc/CreatureQuestLookup.cs` — optional `INpcReactionSource` constructor
   parameter. Without it the old `TryNpcHostility` path is used unchanged; with it both templates
   must exist and hostile means reaction ≤ Hostile. `NpcInfo.FactionId` is now filled.
3. `src/ArcaneCore.World/Npc/QuestNpcFeature.cs` — passes the reputation service into the lookup,
   and into `QuestNpcDependencies.Reputation` only when Faction.dbc is loaded.
4. `src/ArcaneCore.World/Handlers/LoginSequence.cs` — SMSG_INITIALIZE_FACTIONS comes from the
   reputation feature (falls back to the old empty builder).
5. `tests/ArcaneCore.Data.Tests/IntegratedSchemaTests.cs` — characters schema now v7.

## Known gaps (honest limits)

- **Character delete:** `ReputationCharacterDeleteHook` (docs/integration/character-delete.md)
  drains the queue before the deletion transaction removes the rows and queues
  `ReputationFeature.DeleteCharacter(id)` afterwards. Queued post-delete removal has no lifetime
  fence against an explicitly reused id (claude-handoff-20261003.md priority 1).
- **Kill data has no world schema slot.** Kill rewards read `IReputationOnKillSource`; no world
  module provides it in this round, so kill reputation is inactive in the daemon until the world
  data owner adds `creature_onkill_reputation` (or registers a source).
- **Quest reputation rewards** are exposed as `IQuestReputationRewards.RewardQuest`, but
  `QuestTemplate` does not carry `RewRepFaction1..5`/`RewRepValue1..5` yet and quest settlement
  does not call the hook. When wired, the reputation write is a separate queued write, not part
  of the quest settlement transaction.
- No spillover templates (`reputation_spillover_template`), no `reputation_reward_rate`, no
  forced reactions (`SPELL_AURA_FORCE_REACTION`), no aura gain modifiers (e.g. the human
  Diplomacy racial: the spells owner sets `ReputationService.GainModifier`).
- No group, pet or tapped-kill credit: only direct player kills reward reputation.
- No honor-rank vendor discounts (vanilla PvP rank), only the 10 % Honored discount.
- Combat hostility (`CombatHooks`) is not wired to reputation reactions; combat still uses
  template relations. NPCs are not made visible on interaction or attack (only on change), and
  SMSG_SET_FACTION_ATWAR (server-forced war) is not sent.
- The u16/u32 CMSG question above is decided from server sources, not a client capture.

## Tests

- `tests/ArcaneCore.Game.Tests/Reputation/*` — rank boundaries, gray level, quest rate table,
  gains/dither, ReputationMgr rules (defaults, clamp, visibility, war/peace, inactive, watched,
  load restoration), packet layouts and adverse CMSG lengths/slots, reactions both directions
  (GM, contested, at-war cap, template fallback, fail-closed), service notifications, sink rows,
  kill/quest rewards, NPC seam and discount, quest lookup with reactions.
- `tests/ArcaneCore.Data.Tests/Reputation/*` — synthetic Faction.dbc and malformed layouts;
  store round trip, upsert, watched faction, missing character, delete, cross-character rows
  refused, across the SQLite/MariaDB/PostgreSQL matrix.
- `tests/ArcaneCore.World.Tests/Reputation/ReputationWorldTests.cs` — login packet contents,
  socket toggles persisted and restored after a relog, forbidden and malformed toggles ignored,
  empty catalog login.
