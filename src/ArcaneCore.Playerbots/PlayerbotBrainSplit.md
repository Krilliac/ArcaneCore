# PlayerbotBrain and ManagedPlayerbotFeature split

PlayerbotBrain.cs (about 4.1k lines) and ManagedPlayerbotFeature.cs (about 4.5k lines) are partial classes that keep growing. They are split by moving
contiguous member groups into `PlayerbotBrain.<Topic>.cs` / `ManagedPlayerbotFeature.<Topic>.cs` files without changing any member, name or behaviour.

## Done (first tranche)

- `PlayerbotBrain.Contention.cs`: tag contention (IsSomeoneElses, TaggerOf, NoteEngagement, DropIfSomeoneElses, LeaveSomeoneElses, TryHealTagger), the
  fruitless/unreachable/invincible target marks and the fight-progress watch.

## Rules for the next tranches

- Move whole members with their doc comments; never reword or reorder logic. Copy the using block of the source file.
- Keep stable: the `ReadsDifferentlyBaseline.txt` path keys (the baseline has no entry for either class, so a split needs no baseline edit; a member
  that gains an RB001-RB004 entry later is keyed by the file it lives in), the Atlas bookmark ref, and nested exception names.
- ManagedPlayerbotFeature.cs is read as text by `LogSpamCatchContractTests` and `PlayerbotQaSignalParserTests`; move code out of it only together with
  those tests' file lookups.
- Candidates: PlayerbotBrain death/recovery judgement (RecordDeath, JudgeBody), explore-spot choice, quest goal stepping; ManagedPlayerbotFeature tick
  scheduling, fleet bookkeeping, battleground coordinator.
