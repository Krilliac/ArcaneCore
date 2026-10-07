# SpellItemEnchantment.dbc metadata boundary

The pinned vmangos `SpellItemEnchantmentEntry` layout is 24 fields: `ID` 0; `type[3]` 1–3;
`amount[3]` 4–6; `amount2[3]` 7–9; `spellid[3]` 10–12; localized name offsets 13–20;
localized string flags 21; `aura_id` / item visual 22; and `slot` / flags 23.

ArcaneCore now preserves raw uint32 `amount2` values as `AmountMax`, localized flags as `NameFlags`, item visual
as `ItemVisualId`, and field 23 as `SlotFlags`. The existing two-argument synthetic definition
constructor remains valid and supplies empty/zero metadata defaults. These fields are retained as
content metadata only; this slice assigns no gameplay permissions or trade behavior from them.

The bounded reader still relies on the shared `DbcFile` limits (64 MiB and one million records),
rejects non-24-field layouts, and now rejects duplicate entry IDs. Original build-5875 client
content remains pending because the proprietary DBC is not present in the repository.
