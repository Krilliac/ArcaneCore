# Hunter pet abandon and durable deletion

`CMSG_PET_ABANDON` now follows the vmangos hunter split. An owned current hunter
pet (`SummonKind.Pet`, matching the player's `PetGuid`) is queued for durable
deletion, including its cooldown rows, before it is unsummoned. Dead hunter pets
are valid abandon targets. Guardians, mini pets, non-hunter pets, stale GUIDs,
unowned creatures, and held or in-transit owners do not enter the persistent
delete path.

Deletion is admitted synchronously on the world thread: the per-character cache
is cleared and its generation tombstone advances before any database await. The
delete task is installed behind every existing current/detached write in the
same per-character tail and remains visible to `FlushCharacterAsync` and
shutdown. In-flight current or callable reads therefore fail their generation
comparison, while a fresh login waits behind the delete before reading SQLite.

The scoped world feature supplies `IPersistentPetStore.DeleteAsync`. Game tests
cover blocked current-save ordering, late-read invalidation, dead-pet deletion,
guardian no-delete behavior, and stale/unowned refusal. The mock-client test
covers the real socket abandon packet, EF deletion of the pet and cooldown rows,
cold login with no pet, and effect 56 returning `PET_TAME_FAILURE` reason 7.

Pinned references: vmangos `Handlers/PetHandler.cpp:347-368` and
`Objects/Pet.cpp:1023-1138` (`PET_SAVE_AS_DELETED` for hunter pets versus
`PET_SAVE_NOT_IN_SLOT` for other controlled summons).
