# Playable fundamentals from local source data

The observed sandbox had no imported spells or NPC content. A private candidate now contains the selected ClassicDB z2815 world snapshot and a patch-aware build-5875 DBC candidate. Those datasets stay outside the repository and source exports. Header compatibility and database import are separate from original-client acceptance.

The new source connects existing player-stat, NPC and conditions importers to the content CLI and adds starting-skill import/initialization and gender-specific DBC starting outfits. Player stats now populate the database tables consumed by strict stat validation. Opted-in, timestamp-ordered player-stat migrations are reported by filename; an external level-stat CSV reflects their final values.

Starting skills add World schema step 25. Zero race/class masks are wildcards; malformed masks, out-of-range source IDs and rank steps are rejected or reported before narrowed values can turn into valid identities. Duplicate source keys are reported. Persisted and explicitly forgotten skills win over startup defaults. The catalog controls ranges, maximized values and one-based tier steps.

Configured `Items:CharStartOutfitDbcPath` reads the original packed 41-field/152-byte layout. Character creation chooses the exact race/class/gender row, resolves item prototypes and quantities, appends SQL starting items, and reuses inventory placement and persistence. An unset path preserves the existing SQL behavior. Invalid configured data fails closed. Missing item prototypes are skipped with a warning.

NPC import covers direct greeting/menu/options/text/vendor/trainer rows and the existing CMaNGOS-numbered conditions format. Default gossip menu zero is retained. Batched imports protect caller transactions with savepoints and roll back earlier writes on failure. Template inheritance, random spawn-entry alternatives and complete reference validation remain explicit follow-ups; no source row is invented to fill them.

Source contracts use pinned CMaNGOS/vmangos code and the version-checked source snapshot. WoWWiki 1.12.1 and Wowhead Classic remain supplemental references. The private DBC candidate uses an explicit root selection policy of patch-2 over patch over base; the server references do not prove the original client's MPQ precedence. Exact client-content acceptance remains pending.

Local source qualification: 14,771 passed, six existing skips, zero failures; focus1065 plus one existing skip, native59, clean Release. Frozen-source, replay and ZIP gates verified. Populated-profile startup/admission/content/persistence and exact original-client acceptance remain pending. Prior qualified bundles and live profile are preserved.
