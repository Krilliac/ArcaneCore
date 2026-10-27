# Status notes moved out of the README

Internal integration and milestone notes that used to sit in the top-level README. The README keeps only what a user needs.

## Integration candidate

This integration candidate also combines creatures, grids/terrain/teleports, items, spells, social systems, and the partial
quests/NPC draft. Exact branch heads, schema allocations, integration repairs, validation, and remaining client acceptance work are in
[the fleet integration record](fleet-20261003.md). Later NPC, instance, AI, loot, progression, spell persistence and
economy work is in [the takeover ledger](takeover-20261003.md). Continue from
[the Claude handoff](claude-handoff-20261003.md).

## M13a and M13b follow-ups

The [M13a follow-up](../../MILESTONE_M13A.md) restores saved quest journals before login, serves quest/NPC text queries, and persists timed
expiry across relogs and map transfers. Full quest/NPC interaction and [real-client acceptance](../M13A_ACCEPTANCE.md) remain
pending. The [M13b slice](../../MILESTONE_M13B.md) adds guarded live-creature quest status, details, acceptance and abandonment for
ordinary non-repeatable quests; rewards, objective event adapters and broader NPC services remain subsequent work.
