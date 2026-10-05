# Druid form power transitions and Furor (2026-10-04)

The Cat, Bear, and Dire Bear form producer now composes the existing shapeshift holder with the pinned power transitions for any target, matching vmangos' apply path. Cat selects Energy and clears it before Furor; Bear/Dire Bear select Rage and preserve the pre-switch Rage value. Removing a form returns a Druid to Mana and zeros Rage. Non-Druid units retain the existing catalog-backed form/display behavior, while their apply-time power transition follows the source's target-generic behavior.

Furor scans live Dummy auras whose source spell has icon 238, rolls inclusively from 1 through 100 using the SpellSystem random source, and casts the real 17099 Cat or 17057 Bear proc when that SpellInfo exists. Missing proc content is logged and skipped; no synthetic production SpellInfo is generated.

The same apply path now reads `FormBoostTable` for the linked Cat/Bear/Dire Bear, Travel,
Aquatic, Tree, and Moonkin passives. Existing store rows are cast as triggered self spells
with the shapeshift holder as their trigger; missing rows are logged and skipped. Known
form-bound passives and Leader of the Pack (17007 → 24932, gated by the loaded effect
spell's `Stances`) use the same path. On form loss, the exact linked ids are removed before
the normal self-cast/form-bound cleanup and shape-specific cast interruption.

Heart of the Wild (24900/24899) preserves the amount from the live talent aura and injects
that value through a narrow triggered-cast effect override before normal aura application,
so stat handlers record the same amount for exact removal. No database default amount is
substituted and no missing production spell row is fabricated. The override is cast-scoped;
the resulting `SpellAura.Amount` is the persisted snapshot used by removal and any state
capture, so it does not depend on the originating talent aura remaining live.

The implementation follows vmangos `0e3ff01e76d4758e8a7c3108b2717cc785ed56fa`: `Spells/SpellAuras.cpp:2420-2559` for form selection, power reset/preservation, and Furor, `:2607-2616` for Druid-only removal, and `:5433-5535` for linked form boosts. Game tests cover deterministic Furor, wrong-icon/no-chance behavior, Cat-to-Bear replacement, Dire Bear mapping, and power transitions; the World test covers real socket Cat and Bear form producers and removal. Original-client acceptance and complete build-5875 DBC/talent coverage remain pending where those rows are not present in the fixture store.
