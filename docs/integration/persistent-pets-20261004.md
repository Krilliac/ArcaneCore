# Persistent hunter pets and `SUMMON_DEAD_PET`

This slice uses the vanilla distinction between a retained in-world corpse and a cached
current pet. `CreatureMapSystem.TryReviveCurrentPet` keeps the existing world GUID, owner
links, action bar, spell state and retained aura state. The `ICharacterHooks` load runs detached
before world entry, then publishes synchronously on `PlayerLoggedIn`. `RestoreCurrentPet` creates
a pet only from a durable row whose entry is present in creature content; guardians, wild
summons, critters and warlock pets are never written as hunter pets.

The durable identity is the pet number (`character_pet.id` in vmangos), while the world GUID is
transient. State is captured before logout and before map removal; death captures health zero
before corpse decay. Snapshot writes are serialized per character and drained at shutdown;
deletion drains writes, clears pending snapshots, and retains a generation tombstone so an
in-flight read cannot repopulate deleted state. The current row stores entry, level, experience, health, mana, happiness, react
state, action bar and spell/autocast state. Database rows are deleted with the character.

Effect 109 is registered by the pet summon service. It is player/hunter-only, requires a
positive percentage, first revives an in-world dead current pet, and otherwise loads the
current cached row. Health is `maxHealth * effectValue / 100`, clamped to at least one. This
matches vmangos `SpellEffects.cpp:5533-5570` and mangos-classic
`SpellEffects.cpp:5163-5199`; both references identify effect 109 as
`SPELL_EFFECT_SUMMON_DEAD_PET`. vmangos also confirms the current-pet cache and pet-number
lookup in `Objects/Pet.cpp:120-200`.

The characters schema module is composed at version 21 in the current branch. No external
provider qualification is claimed; the cold-store test is SQLite-backed.

This is a thin current hunter-pet lifecycle slice, not a complete hunter-retail implementation.
Pet spell cooldowns, saveable non-passive auras, loyalty/training, tame/stable subtype, pet type,
names, and related producers remain outside this slice; see `work/pet-state-next.md`.

Suggested coordinator checks:

```powershell
dotnet test tests/ArcaneCore.Data.Tests/ArcaneCore.Data.Tests.csproj --filter PersistentPet
dotnet test tests/ArcaneCore.Game.Tests/ArcaneCore.Game.Tests.csproj --filter "PersistentPet|SummonDeadPet"
dotnet test tests/ArcaneCore.World.Tests/ArcaneCore.World.Tests.csproj --filter PersistentPetWorld
```
