# Asynchronous ordinary quest settlement

This extends the qualified ordinary creature-kill/item/money reward slice. Reward
categories and the explicit `Quests:OrdinaryRewardQuestIds` allowlist stay unchanged.
No clustering transport or process deployment is introduced. The reward transaction also
carries learned spells and faction rows (below); the only schema step is world v9, which adds
the quest reputation reward columns to `quest_template`.

## Ownership and ordering

The world thread prepares the exact reward inventory, chosen item entry, money and
journal transition. It assigns an operation GUID and freezes the original Player.
The feature allows eight concurrent character settlements, one per character; excess
requests leave the character active and can be retried. It retains the same prepared
request and allocated item GUIDs until that operation finishes.

Storage runs on observed worker tasks. The captured core Before snapshot is saved
first through the ordered retaining queue, then outstanding quest writes drain.
After both complete, core and quest persistence are quarantined. A fresh scoped
reward store commits character state, inventory, the rewarded journal row, the spells a
reward spell teaches and the faction rows a quest reputation reward leaves in one
serializable transaction. The existing non-repeatable rewarded row is the durable
idempotency key; the operation GUID protects in-memory publication and ownership.

The callback returns to the world thread. It checks the retained operation and exact
live Player before applying anything. Its publication scope permits only the
synchronous prepared reward while the character remains pending to gameplay
observers. Holds are released after successful publication. A disconnected Player
receives no reward mutation or success packet; durable data is loaded on fresh login.

## Mutation and recovery boundaries

Pending character packets are drained and dropped within the ordinary per-tick
budget, so old actions do not replay against the reward result. Quest operations,
inventory/money writes, combat on either participant, spell effects and periodic
caster/target pairs, and teleports respect the hold. Existing teleports exclude
settlement initiation. Disconnect teardown still removes combat and map ownership.
Autosave/logout check the hold before creating snapshots and consuming dirty flags.

An uncertain commit is read from fresh scoped stores. Exact Before permits ordinary
recovery; exact After adopts the rewarded cache and publishes only to the original
live owner. An unreadable or different outcome keeps persistence quarantined and
requires fresh login. Relog waits for storage and publication before fetching fresh
character/inventory/quest data; only successful full entry resumes writing. Stale
save/remove callbacks cannot affect a replacement Player sharing its GUID.

Shutdown cancels cooperative I/O and observes worker completion after the world
stops. It finalizes durable cache state without awaiting another world tick, and
quarantined caches/snapshots cannot be flushed over an uncertain durable reward.

The five-second storage budget is cooperative. An underlying store that ignores
cancellation remains awaited; its character and one of the eight slots remain held,
and shutdown waits for that operation to terminate. Quest draining likewise observes
the underlying write to completion. This avoids an abandoned transaction racing a
retry. It does not promise a hard timeout for a non-cooperative provider.


## Reward collaborators: spells, items and reputation

Before the character is held, `TryPrepareReward` also asks the reward-spell owner
(`IQuestRewardEffects.TryPrepareRewardSpell`) and the reputation owner
(`IQuestReputationSettlement.TryStage`). A refusal returns before any hold: the journal
row, inventory and reward choice are untouched and the reward stays available (see
[quest-progression.md](quest-progression.md) for the accepted spell shapes and the
preflight). What they return is frozen in the plan: the spells to learn, created-item
counts (appended to the staged inventory grants) and the faction rows computed on a copy
of the player's standings.

The settlement worker, after the core and quest drains and inside the same 5 s budget,
also drains the owners whose rows the transaction writes: the spellbook
(`SpellbookCache.FlushCharacterAsync`, which throws when a failed write cannot be
recovered) and the reputation queue (`ReputationFeature.FlushAsync`). A failure there
leaves the outcome NotStarted: no transaction, character resumed, journal preserved.
Anything later written for the character is held out by the guards (`ForgetSpell`,
`.unlearn`, reputation changes, flag toggles; `SetWatchedFaction` is not guarded, it writes
its own table). The flush does not prove storage equals live reputation: a queue write
dropped after three retries stays dropped (handoff priority 2), and only the factions the
reward writes converge.

The commit inserts the missing `character_spell` rows and upserts the `character_reputation`
rows through the context's own sets in the same single `SaveChanges`. The module stores
cannot be used: they call `SaveChanges` and clear the tracker, which would flush or drop the
staged character, inventory and quest changes. The quest row stays the idempotency key.

Reconciliation after an uncertain commit reads the spell and faction rows as well. Before keeps
its rule (atomicity means none of ours was written). After additionally requires every learned
spell to be present and every staged faction row to match exactly (standing and flags); anything
else is Unknown and keeps the character quarantined until a fresh login.

Publication, for the exact live Player only: `AdoptRewarded` and the spellbook cache adoption
happen for any outcome After (even for a disconnected owner); inside the publication scope
`ApplyReward` applies money, inventory and XP, sends `SMSG_LEARNED_SPELL` for the learned spells,
replays the frozen reputation gains and sends `SMSG_QUESTGIVER_QUEST_COMPLETE`; after the hold is
released the passive self-casts and any transient reward spell run once behind the
`EffectsPublished` flag. A replacement Player receives none of it: login waits for the settlement,
then the spell and reputation loaders read the committed rows.

## Acceptance evidence

Qualification exercises real disposable EF transactions, delayed SaveChanges,
cancellation/error rollback and retry, lost acknowledgements, unreadable committed
After state, disconnect/relog, retained pre-transaction fields and stale callbacks.
The responsiveness case holds a scoped real reward operation for at least 650 ms
while measuring actual map ticks, world commands and another authenticated player's
map-dispatched NPC status and quest acceptance. Each measured response has a 200 ms
budget; actual metrics and final run/commit evidence are recorded in the integration
ledger and task outcome.

Automated success does not establish real-client rendering or quest UI acceptance.
The [client handoff](quest-client-acceptance.md) defines a productive bounded baseline
session and the separate content/network prerequisites for ordinary reward UI.
Existing aura caster attribution through GUID-only session replacement remains a
separate spell-lifecycle issue; it is not expanded by this settlement work.

Reward-collaborator evidence (real SQLite EF over loopback sockets): a LearnSpell and a CreateItem
reward settle atomically with the journal; a commit held while the owner disconnects is adopted by
the replacement login without replay; a lost acknowledgement reconciles to After only when the
learned-spell and faction rows are present, and a missing row ends as Unknown (kicked, no
publication); a failing spellbook retry stops the settlement before any transaction and the same
reward then settles after recovery; a known teleport destination is published once to the original
player and an unknown map is refused before any hold; a reputation reward persists with the journal
and survives a relog. The Data tests cover rollback, detached lists and the single `SaveChanges`.
Hosted MariaDB/PostgreSQL provider cells were not run in this lane.
