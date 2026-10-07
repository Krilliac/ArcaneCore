# Food and drink regeneration — 2026-10-04

Aura 84 (MOD_REGEN) contributes health on the existing two-second out-of-combat
player tick. Its amount scales by tick/period, with a 5000 ms period when the
content amplitude is zero. Sitting multiplies the existing spirit contribution,
not the food contribution. Health fractions retain the existing carry.

Aura 85 (MOD_POWER_REGEN) contributes mana as amount per five seconds, independent
of amplitude. The flat mana contribution survives the five-second rule and uses
the configured mana rate. Spirit and flat mana are combined before truncation.
Other powers are excluded from this slice; rage Anger Management is a distinct
periodic handler and remains pending.

The existing SpellSystemPowerAuras bridge reads actual live holders. Default
interface implementations preserve clients that provide only the original aura
queries. The discovered FoodDrinkAuras module registers these modifier types;
it does not create a second ledger. FoodDrinkFeature observes Player stand-state
transitions and removes standing-cancelled holders through the existing interrupt
pipeline. Positive incoming damage already stands a seated player.

Pinned vmangos references: Player.cpp:2269-2406; StatSystem.cpp:642-663;
SpellAuras.cpp:4795-4855. Game tests drive actual combat ticks with live spell
holders and cover period defaults, matching power, mana rate, recent mana use,
and combat. World coverage sends CMSG_USE_ITEM for a combined food/drink item,
checks both resources, and sends CMSG_STANDSTATECHANGE to remove the holder.

Food/drink emotes and heartbeat visual kits, combat health regeneration modifiers,
polymorph regeneration, and mana spirit percentage/interrupt modifiers remain
separate work. The modifier registration is not proof of those additional
behaviors or original-client acceptance.
