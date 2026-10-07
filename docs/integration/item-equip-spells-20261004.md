# ON_EQUIP item spells (2026-10-04)

Inventory now exposes a nullable `IItemEquipSpellSink` bridge. It is invoked
from successful `ApplyMods` transitions, including equip/remove, break/repair,
and equipment restoration; shadow inventories leave it null, so staged
reward/economy planning cannot apply world auras before commit. The world
`ItemEquipSpellFeature` binds the sink after login and reapplies equipped,
unbroken items after loading hooks, then delegates subsequent mutations to the
spell system.

The spell-system applies only `ItemSpell.Trigger == ON_EQUIP`, retains
item GUID provenance on the resulting aura, and removes by item GUID. This is
necessary for two equipped items carrying the same spell. ON_USE spells,
including negative-charge use auras, remain outside the equip callback and are
not charged or removed by unequip.

Equip casts bypass ON_USE eligibility, charges and cooldowns. Their holders are
excluded from ordinary aura persistence and rebuilt from durable equipment on
login, avoiding orphaned equip effects. Same-spell holders retain separate item
owners. The pinned vmangos ON_USE unequip exception is broader than its preceding
negative-charge comment: its additional Nostalrius branch skips all ON_USE spells.

Ground truth: vmangos `Player.cpp:7148-7240` and item-fit rechecks at
`Player.cpp:19849-19860`. Game tests apply/remove actual auras and exercise
duplicate ownership and break/repair. World coverage sends swap/auto-equip
requests and verifies exactly one original item owner after relog. Qualification
is recorded in the wave evidence. Form-change re-evaluation, item-set effects,
combat proc ownership, and original-client acceptance remain separate work.
