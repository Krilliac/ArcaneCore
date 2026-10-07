# Item cooldown owners (2026-10-04)

The runtime now carries item cooldown owner metadata alongside persisted spell
and category cooldown durations. An owner records item entry, effective
category, and owning spell ID; category-only survivors can therefore rebuild an
initial-spells row after the spell cooldown expires. Ordinary spell casts retain
item entry zero and fall back to Spell.dbc category data.

The character state row carries the owner fields through the existing atomic
spell-state save/load path. The composite identity includes item entry so two
item-caused cooldowns for one spell are not collapsed. `ItemCooldownOwnerDataModule`
defines the schema-slot-22 owner table contract (`character_item_cooldown_owner`)
with spell/item identity and both Unix expiries; the central schema manifest
still owns final registration and reconciliation, which this lane did not edit.

Ground truth: vmangos `Player.cpp:3929-3981` loads
`spell_expire_time`, `category_expire_time`, and `item_id`; `Player.cpp:3994-4018`
writes the same owner identity and both expiries. Current verification remains
coordinator-owned: build/test, SQLite cold-store round trip, relog, initial
spell packet reconstruction, category expiry outliving spell expiry, unknown
item/spell cleanup, and real 1.12.1 client acceptance.
