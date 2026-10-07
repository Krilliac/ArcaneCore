# Carried recovery and EventAI caster movement

Managed playerbots choose carried food below 45% health and drinks below 35% mana.
Food has priority when both resources are low. Classification uses the loaded item
and spell catalogs: one normal on-use spell, one supported positive regeneration
effect, generic spell family and standing interruption. Other power types and mixed
effects are excluded. Purchasing still uses the existing starter-food policy.

The bot stops, sits and uses the ordinary managed item packet. Inventory consumption,
cast legality and regeneration remain owned by the normal world handlers. Recovery
waits at most 20 seconds and ends on combat, aura removal or restoration of the
resource being recovered. The start thresholds are not the stop thresholds.

EventAI action 57 implements type 0 and type 3 only. Type 3 supplies an approach
distance and retains ranged mode when the victim closes; it neither forces retreat
nor changes action 20's independent melee policy. Modes 1, 2 and 4 remain unsupported.
The movement adapter retains ordinary pathfinding, casting and crowd-control checks.
Action 11 with main-spell flag 0x100 still delegates an ordinary, non-triggered cast
to the spell system. Broader main-spell fallback transitions and modern spell lists
remain pending.

This contract is grounded in the pinned CMaNGOS `UnitAI::SetRangedMode`,
`SetCurrentRangedMode`, `UpdateAI`, and EventAI action processing, plus ArcaneCore's
existing food/drink world handlers. The dedicated quest-15 fixture separately checks
the explicit reward allowlist, prerequisite journal and durable settlement. Test
fixtures and local qualification do not imply original-client acceptance.
