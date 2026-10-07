# Group pet body and vitals (2026-10-04)

Group member stats now source pet GUID, model, health, max health, power type,
and current/max power from the live current pet. Missing or stale current-pet
identity serializes as empty/zero fields. Pet body changes are included in the
existing out-of-range `MemberStats` diff; a power-type change carries both
current and max power bits. Pending pet-name changes remain merged into the
same packet.

The wire order and widths follow vmangos `GroupHandler.cpp:599-735` and the
build-5875 `smsg_party_member_stats.wowm` contract. Pet numeric values use
explicit unchecked u16 narrowing. Pet aura masks and pet-buff integration remain
outside this slice.
