# Enchantment catalog reload

`spell_item_enchantment` reloads the optional configured build-5875 DBC;
`spell_proc_item_enchant` and `spell_enchant_charges` read their SQL stores in
independently disposed asynchronous service scopes. Absent sources are excluded
from `.reload all`. Present empty SQL content clears its overlay. A failed build
retains the published catalog. Each world-thread commit has transaction rollback.

Online players use a shared provider for future enchant applications. Already
applied effects retain the original definition and equipment-slot snapshot until
removal; removal reverses the actual applied amount. The next application uses
the current definition. Reload does not retroactively change active item bonuses.

Reload-all orders charges after `spell_template` when both are included. Each
reload remains its own transaction; the whole batch is not atomic. Cyclic
dependencies reject the batch before builds. An omitted dependency uses current
live content. `.reload spell` is now ambiguous; use the full name or `spell_t`.

DBC metadata preserves unsigned maximum amounts, localized-name flags, item
visual id and final slot flags. These raw fields do not authorize trade ownership
or new binding behavior. The original 1.12.1 client/DBC acceptance remains pending.
