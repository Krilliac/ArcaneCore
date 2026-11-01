# Pets: unsummon reasons, bot autocast, pet data import

The pet model itself (summon kinds, charm info, happiness, stable) is described in `docs/integration/pets.md`; this page covers the
operational notes added since.

## Unsummon reasons

`SummonService.Unsummon(creature, PetUnsummonReason)` (`Game/Pets/PetUnsummonReason.cs`) removes a summon and logs why. The overload
without a reason logs `Unspecified`. Every reason is logged at Debug except `RanAway` (a hunter pet whose loyalty ran out, SMSG_PET_BROKEN),
which is Information, so a busy world's routine departures (a dismissed or replaced pet, a timed guardian, an owner who teleported) stay out
of the normal log. Reasons: `Dismissed`, `Replaced`, `OwnerLeft`, `OwnerDied`, `CorpseDecayed`, `Expired`, `RanAway`, `Unspecified`. Callers
that were not tagged (possession release, talent hooks, item scripts) still log `Unspecified`.

## Bot pet autocast

A pet's castable spells start with autocast off (vmangos `ACT_DECIDE`, `Pet.cpp:1887-1975`). A managed bot turns on the harmful ones
(`PlayerbotPetControl`, `Playerbots/PlayerbotPetControl.cs`) with CMSG_PET_SPELL_AUTOCAST on each think of a class rotation, so a hunter
pet's Growl and Claw and a demon's Firebolt are cast by `PetAI`. A spell is chosen by id and by its data (not passive, not positive), never
by name. Once a spell is on, the bot sends nothing more for it. Known limit: any harmful pet spell is enabled, including ones a
player would keep off (for example a threat-reducing Cower); there is no per-spell deny list.

## Pet data import

`import` reads `pet_levelstats` and `petcreateinfo_spell` from a cmangos classic-db or vmangos dump (`PetDumpImporter`); the dumps are
never committed and the repo ships no pet create-spell content file.

* The importer reads every dump file given before it writes; the order of files does not matter except that a later row with the same key
  (`(entry, level)` for stats, `entry` for create spells) replaces an earlier one. List the classic-db dump first and a vmangos dump after it
  when the vmangos rows should win.
* The write is one transaction. Without `--replace` a key that already exists in the world database fails the whole import and nothing
  changes; re-importing into a world that already has pet rows therefore needs `--replace`, which empties both pet tables first.
* A creature with level stats but none for level 1 (health above 0) is dropped with a warning, because the world refuses to start with such
  a table (vmangos exits at `ObjectMgr.cpp:4492`).
