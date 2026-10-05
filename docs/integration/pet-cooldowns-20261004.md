# Current hunter-pet cooldown persistence

The current pet snapshot now carries optional spell and category cooldowns as wall-clock Unix
millisecond expiries. Capture uses the existing `SpellSystem.CaptureState` producer while
the live pet is still mapped; restore maps the records to the existing
`RestoreCooldowns` primitive. Expired rows and unknown spell IDs are skipped by that primitive.
`SMSG_PET_SPELLS` already consumes `GetActiveCooldowns(pet)` and therefore publishes restored
cooldowns through the existing packet trailer.

Characters schema version 23 adds `character_pet_cooldown`, keyed by character, stable pet
number, cooldown kind, spell, and category. Version 22 is owned by another lane. The cooldown
table is deleted with the character and when replacing the current snapshot. No aura, loyalty,
training, tame/stable subtype, or pet-type persistence is included.

Ground truth: vmangos `Pet.cpp:1526-1572` loads pet cooldowns and `:1585-1605` writes them;
`Pet.cpp:621-626` deletes them with the pet. ArcaneCore’s generic cooldown semantics are in
`Game/Spells/SpellSystem.Persistence.cs` and the pet packet layout in `Game/Pets/PetPackets.cs`.
Provider validation remains limited to the configured SQLite cold-store tests; no external DB
qualification is claimed here.
