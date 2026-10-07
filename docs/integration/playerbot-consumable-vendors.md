# Playerbot consumable vendors

Playerbot town acquisition uses the same real item-spell classifier as carried-item recovery. A vendor row is eligible only when its live item template has exactly one normal slot-zero spell, the spell is present in the active catalog, has the standing-cancel interrupt flag, and applies one positive food or mana-regen aura. Item names, `FoodType`, and entry IDs are not used; item `117` remains supported through its catalog spell.

Discovery and execution share one bounded selector over the first 32 vendor rows. It checks the real item price and current money, prefers a missing food candidate, and considers drinks only for mana-capable players without a usable classified drink. Existing inventory items are considered available when ordinary `CanUseItem` permits them; cooldown readiness remains authoritative in the normal item-use spell path, so a cooldown-refused item does not cause duplicate purchasing.

The advisory `GetVendorPurchasePrice` query shares ordinary visibility, reputation, stock,
discount and storage rules without requiring interaction distance or buying anything. Limited
stock uses the existing lazy restock calculation. The ordinary `CMSG_BUY_ITEM` path rechecks
the offer and owns final settlement. Missing SpellFeature or item content fails closed.

Classified food and drinks are protected from gray-item selling, avoiding a buy/sell loop.
Existing trainer, repair and quest-item protection remains in place. Vendor discovery also
checks the template's level, class, race and skill requirements before proposing a purchase.
