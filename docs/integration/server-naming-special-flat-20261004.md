# Verified continuation: pet naming, special procs and flat regeneration

Coordinator verification on 2026-10-04: Release zero warnings/errors; one
uninterrupted final six-project suite 14,453 passed / six existing fixture skips /
zero failures; native mock self-test 59 checks. Focused Game50 / World13 /
Data15 / SQLite cold-host3 checks pass. Initial development failures remain
separate evidence, including the missing schema-cleanup declaration.

Characters schema24 adds pet name, timestamp and rename permission columns.
The SQLite23-to24 upgrade preserves rows and defaults. These columns belong to
the pet row already deleted by PersistentPetDataModule; naming declares this
cleanup relationship. Mutable names preserve the submitted casing, validation
is world-local, and external profanity/reserved catalogs remain pending.
Rename/abandon client flags survive initialization; the saved timestamp is
restored to the actual unit field as well as the name-query response. The real
socket rename/query acknowledgement precedes flush and a cold host reload.

The targeted review found that detached dead pets could not reach effect109.
Revival now spawns before promoting and serializing a fresh current snapshot,
retaining identity, name, timestamp and cooldowns. Cold socket/SQLite checks
verify durable promotion. Living cached pets refuse revival with reason11,
following vmangos Spell.cpp6073-6115 and SharedDefines.h1720.

Combat-range weapon spells produce trigger2 item casts after target effects
and ordinary misses. Offhand, reflection, self and lethal-target behavior follow
the pinned source. Item GUID, cooldown provenance and cost rules remain intact;
custom attributes, enchantments, ranged producers and extra attacks stay pending.

Aura161 adds Rate.Health *2*sum/5 to each two-second health tick. Rate.Health
scales spirit and this flat term, with food remaining unscaled and out of combat.
Real aura/tick tests cover stacking, removal, fractional carry, health caps and
negative modifiers while preserving combat rage. Aura88 and polymorph remain
separate source-backed follow-ups.

Base remains7313b9eb089197b253dc51bd8bb5ceb803adcc6b on
codex/server-continue-20261004. Source is local, unstaged and uncommitted.
Original-client acceptance, proprietary fixtures and external database-provider
qualification remain pending. No live deployment/service change occurred.
WoWWiki1.12.1 and Wowhead Classic remain version-checked research sources.
