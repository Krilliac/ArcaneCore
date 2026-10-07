# Item payment planning and final trade refusal

ItemUsePaymentPlan is a pure preview of the existing five-slot item-use charge
rules. Positive charges decrement, negative charges move toward zero, stackable
items retain charge fields, and the final charged slot decides expendable deletion.
The before/after snapshots copy charge and enchant arrays; no live item is changed.

TryPlanTradeItemEnchantment authorizes an owned item's on-use template spell
independently of the player's spellbook. This bounded planner requires exactly
one applicable on-use spell and one permanent/temporary enchant effect. It shares
the existing after-image helper, preserves item-template cooldown metadata and
reagent overlap, and skips item-cast power costs. It does not bind, pay, publish
or register a World item-use callback. Multi-spell, mixed-effect and randomized
enchant durations remain refused. CMSG_USE_ITEM trade execution is still disabled.

Final spellbook trade refusal now clears only the failing owner's pending spell
and refreshes both views, matching pinned TradeData::SetSpell(0). Both acceptance
flags clear; the partner's intent remains. Gold, unaccept and cancel-cast preserve
deferred intent according to the pinned handlers. A new request is required to
replace a failed owner's cleared intent.

This slice leaves the previously published costs/SpellGo behavior intact. Full
cast-item settlement wiring, binding policy, original-client/DBC acceptance,
external providers and durable crash publication remain pending.
