# GM aura inspection

The read-only `.auras` command displays active aura holders on the selected online player, or on the GM when no player is selected. It requires the existing GameMaster account rank and applies the normal `CanActOn` security rule before reading another player's state.

Use `.auras` for the first page and `.auras 2` (through page 100) for later pages. Each page contains at most 12 live holders. A holder reports its numeric spell ID and spell name, caster GUID, remaining duration in milliseconds (`-1` means permanent), and up to three actual effect rows. Effect rows include the effect index, numeric and named aura type, applied amount, and effect misc value. The misc value is preserved as the spell system reports it; for faction or reputation auras this is the relevant faction/value field where the spell provides one.

The command reads `SpellFeature.System.GetAuras` on the world thread, filters removed holders, and does not cast, remove, refresh, mutate, or persist aura state. An empty or out-of-range page produces a bounded diagnostic reply. Caster GUID is reported even when a persisted aura no longer has a live caster object.
