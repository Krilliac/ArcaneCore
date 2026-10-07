# Cooldown owners, pet cooldowns and consumable targets — 2026-10-04

Characters schema 22 adds `character_item_cooldown_owner`; schema 23 adds
`character_pet_cooldown`. The original cooldown row shape and key are preserved.
SQLite coverage upgrades an actual schema-21 prefix, preserves an existing
cooldown row, and round-trips the new item owner data through a fresh context.

Item casts retain item entry, effective category and owning spell identity.
Independent spell/category Unix expiry fields survive logout. Category-only
survivors retain owner metadata and reconstruct `SMSG_INITIAL_SPELLS` after the
spell timer expires. Multiple item spell owners can share a category; clearing
an item cooldown uses its effective category. Unknown owned spell records are
skipped. The owner key reflects the current producer's single owner per spell;
future changes to that producer require reconsidering the storage identity.

Current hunter-pet snapshots now capture live generic spell/category cooldowns.
Cached restoration resumes those timers before `SMSG_PET_SPELLS` publication.
Both future and expired cooldown cases pass across actual host disposal and
fresh SQLite-backed host startup. Pet replacement is transactional and detaches
its tracked rows after success or failure; rollback and same-context retry tests
preserve the previous snapshot and cooldowns.

Consumable resource checks now evaluate the current spell and actual target.
A single mixed heal/energize spell succeeds when either resource needs filling;
the last applicable full-resource effect determines the failure when neither
can restore anything. Invalid power indices are refused safely. Pet-target
effects are skipped in this check, following the vanilla source. Lifecycle and
trade guards precede binding; resource checks follow binding in the cast path.

The Release build has zero warnings/errors. Focused checks pass 33 Game, 12
Data, three World and six MockClient cases. The final six-project aggregate
qualifies 14,356 passing tests with six existing fixture skips. One initial
quest-capacity timeout is retained in the evidence: its focused case passed in
24 seconds and the unchanged full MockClient project passed all 202 tests on
rerun. The five other passing projects were retained without redundant reruns.
The timeout's cause is unproven; it is not claimed repaired.

Native mock verification, source digest and selected TRX files are retained in
the portable output manifest. Actual build-5875 client acceptance and the
external MariaDB/PostgreSQL provider matrix remain pending. No live service,
push, merge or deployment was performed.

Pinned vanilla source references remain authoritative. WoWWiki 1.12.1 and
Wowhead Classic support version-checked historical research under the charter's
corroboration policy.
