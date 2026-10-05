# Anger Management aura slice — 2026-10-04

The aura module now owns `ModPowerRegen` ticking separately from food/drink
visuals. Rage-targeted effects require an alive target and matching current
power, then apply vmangos' `amount * 3 / 5` raw rage operation. Mana and other
powers receive no periodic resource mutation from this handler.

Root-owned aura construction supplies the source defaults: rage amplitude/period
3000 ms when unset, non-rage 2000 ms, and the initial periodic timer 5000 ms.
The 5200 ms unit heartbeat remains independent. Persistence restores the aura
timer remainder through the existing aura state path.

Focused Game and World tests cover the 4999/5000 ms first boundary, subsequent
3000 ms cadence, dead/mismatched no-op behavior, and real world spell production.
Full build/test verification remains coordinator-owned.
