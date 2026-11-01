# World-thread audit lane 5: enchant sweep

Finding 10.1 of the world-thread audit: `PlayerEnchantments.Update` ran `SweepTimers` once a second for every player, walking every held item times seven enchantment slots, because items enter and leave the inventory without a hook into the enchantment engine.

## Verdict

Confirmed on current code. The sweep itself is needed (a timer must start for any held item with a timed enchantment and stop for any item that left), but it only has work to do when the inventory changed.

## Change

- `PlayerInventory.LayoutVersion` (internal) moves on every write of an own slot (store, removal, buyback), every store or removal inside a bag (`Container.StoreItem` / `Container.RemoveItem`), and at the end of `Load`. It is bumped in those lowest-level writers, not in their callers, so no item path can change what the player holds without it.
- `PlayerEnchantments.Update` still looks at the gate once a second, but runs the sweep only when the layout version moved, when a timed enchantment was written through `ItemEnchantments.Set` (`RequestSweep`), or when the backstop is due.
- Backstop: 30 s plus a per-character offset of 0 to 15 s (the stagger helper returns a value below half of the interval it is given, 30 s here; `WorldRuntime.AutosaveStagger` of the character id), so characters that logged in together do not sweep in the same tick. It covers paths that write the item fields directly.
- Counters: `PlayerEnchantments.SweepsRun` and `SweepsSkipped`.

Single-writer-per-map invariant: unchanged; everything runs on the map thread inside the existing updater, the new state is per player and per inventory.

## Tests

`tests/ArcaneCore.Game.Tests/Crafting/EnchantSweepGateTests.cs`: unchanged bags run no sweep between backstops; an item moved or stored after the last sweep gets its timer within one sweep; a destroyed item drops its timer; a timed `Set` is found without an inventory change; the backstop catches a raw field write. Before the change every one-second check swept, so the "unchanged bags" counter test and the "raw write is not found until the layout moves" assertions fail on the old code (the counters and `LayoutVersion` did not exist, and the raw write was found at once).

## Not done

- A per-minute log line or `.debug maps` display of the counters; the counters are exposed per player only.
- The per-updater `EnchantUpdater` timing is already in the slow-map breakdown (`MapUpdateDiagnostics`).
- Tests were written but not run in this lane (no builds in the lane); they run in the integration gate.
