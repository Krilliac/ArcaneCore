# SQL exact reserved-name policy (2026-10-04)

World schema 21 adds the source-shaped reserved_name table with a required
name key limited to 12 characters. Characters schema remains 24. No original
reserved rows are bundled. EF loading uses a registered WorldDbContext factory,
creates/disposes a context per load and returns a frozen normalized set. Strict
UTF-16 decoding rejects malformed values; length counts Unicode code points.

NameCatalog composes exact case-insensitive SQL membership with optional DBC
regex catalogs. Startup reads the SQL store inside a service scope; character
creation and pet naming consume the same immutable policy. Empty/unconfigured
stores preserve existing behavior. Source pins: vmangos ObjectMgr.cpp
9454-9504, CharacterHandler.cpp 264 and PetHandler.cpp 318-334.

Data tests prove a real SQLite schema 20-to-21 upgrade preserves an existing
world row, loads the EF store and keeps the returned snapshot immutable.
Production DI registration is checked with scope validation. World tests prove
complete character-create denial without a persisted rejected row and actual
SQLite-backed pet rename denial followed by an accepted name. Staff/GM bypass
parity, live snapshot reload and external database-provider qualification remain
pending; the present SQL policy applies to every caller.
