# Quest and NPC daemon adapters

M13a loads and persists journals, serves quest/text queries, and expires timed quests.
[M13b](../../MILESTONE_M13B.md) adds the bounded live-creature status/details/accept/abandon
surface. [Acceptance](../M13B_ACCEPTANCE.md) documents the mock and later client procedures.

The creature adapter reads the actual player map, visible GUID set, creature template entry,
spawn ID, current NPC flags, health/death state, combat, selectability, charmer, position, and
bounding radius. An injectable immutable catalog or optional developer-supplied
FactionTemplate.dbc establishes hostility. Unknown, nonzero NPC faction IDs, and contested-guard
templates deny interaction until their reputation/PvP state adapters exist.

No schema version is introduced. CreatureTemplateRow's version-2 model remains unchanged.
Gossip-menu and trainer metadata do not exist in that model; these handlers never consume those
fields or register gossip, vendor, trainer, completion, or reward behavior. Non-repeatable journal-only quests
can be accepted and abandoned completely without inventing source-item or spell effects.

Related quest details do not require acceptance eligibility, as in the pinned read-only query
handler. Mutating acceptance enforces that eligibility. Status reads visible known-faction
creatures independently of interaction range/liveness. Repeatables remain outside journal
mutation until their rewarded history can be handled by completion and timer rules.

Remaining work: establish Faction.dbc/reputation state, import actual gossip/trainer metadata via
an additive upgrade, connect item/spell/combat objective events, and provide transactional
reward collaborators. These require separate bounded slices and evidence.
