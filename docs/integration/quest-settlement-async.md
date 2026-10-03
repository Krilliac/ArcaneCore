# Asynchronous ordinary quest settlement

This extends the qualified ordinary creature-kill/item/money reward slice. Reward
categories and the explicit `Quests:OrdinaryRewardQuestIds` allowlist stay unchanged.
No clustering transport, process deployment, or schema migration is introduced.

## Ownership and ordering

The world thread prepares the exact reward inventory, chosen item entry, money and
journal transition. It assigns an operation GUID and freezes the original Player.
The feature allows eight concurrent character settlements, one per character; excess
requests leave the character active and can be retried. It retains the same prepared
request and allocated item GUIDs until that operation finishes.

Storage runs on observed worker tasks. The captured core Before snapshot is saved
first through the ordered retaining queue, then outstanding quest writes drain.
After both complete, core and quest persistence are quarantined. A fresh scoped
reward store commits character state, inventory and the rewarded journal row in one
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
