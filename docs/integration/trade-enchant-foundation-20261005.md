# Deferred trade enchant settlement foundation

Build-5875 `TRADE_ITEM` carries packed raw slot value 6, not the partner item
GUID. A World-owned callback stores one pending enchant per trade side. Wire
parsing cannot supply the foreign item reference. Strict acceptance validation
resolves the partner's slot-6 item on the server and reuses spell cast gates.

The inventory stage composes an enchant after-image and carried reagent debit
with ordinary transfers in complete Before/After participant snapshots. Planning
does not change live inventory. The existing serializable economy transaction
commits the snapshots together. Live publication retains the target Item identity
and reverses/reapplies enchant sinks; failed storage leaves Before authoritative.
Unknown results and publication failures retain the existing quarantine policy.

Pinned vmangos TradeData distinguishes invalidation: real item changes clear the
changing side's spell; changing slot6 also clears the partner's. Gold changes and
unaccept preserve pending spells. Replacing a spell clears both acceptances and
updates both trade views with the spell id. Closing the trade clears pending state.

This is a limited foundation. Only one permanent or fixed-duration temporary
enchant effect with zero calculated power cost, no recovery/GCD metadata and no
cast-item identity is accepted. Random durations and mixed effects are refused.
Durable power/cooldown/cast-item costs and normal final cast visual packets remain
pending. The current stage conservatively refuses reagent/replacement conflicts
and may refuse some otherwise satisfiable carried/equipped reagent arrangements.
Full original-client trade-enchant acceptance is not claimed.

Focused tests exercise raw packets, pending views, gold/unaccept preservation,
slot6 invalidation, real SQLite success with durable enchant/reagent changes,
final missing-reagent refusal and storage conflict without partial publication.
World24 and Characters24 schemas are unchanged.
