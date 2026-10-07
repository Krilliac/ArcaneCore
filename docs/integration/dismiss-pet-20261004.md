# Hunter dismiss and detached callable pets

Effect 102 now has a narrow hunter-only path. A live owned `SummonKind.Pet` is captured as a
detached snapshot (`IsCurrent=false`) before unsummoning. The detached row remains callable but
is excluded from login auto-restoration. Effect 56 consumes the callable cache/store and promotes
the snapshot through the existing world-thread restore path.

The distinction follows vmangos `Pet.cpp:137-161`: login reads slot 0/current, while Call Pet
selects current or another non-stabled pet. `SpellEffects.cpp:5134-5148` refuses missing/dead
pets and uses `PET_SAVE_NOT_IN_SLOT`; hunter abandon remains a separate unimplemented pet-store
operation. Existing schema 21 `IsCurrent` represents both states; no new schema allocation is
needed. Detached-store methods are additive and unsupported stores fail closed.

Tests cover SQLite current-versus-callable selection and Game effect 102 detach followed by effect
56 restoration. Full cold-host/logout/socket validation remains the coordinator's integration
boundary.
