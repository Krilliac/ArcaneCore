# Persistent pet cold-host acceptance — 2026-10-04

The owned mock-client lane now extends the existing SQLite loopback lifecycle
fixture as a partial test. It seeds an explicit synthetic family-bearing
`creature_template` row into the fixture's WorldDb before host startup, creates a
Hunter character, restores a current pet snapshot into the live world, waits for
logout completion and host shutdown, then starts a fresh `PersistentHost` against
the same database. The passing test verifies stable pet number, health and Hunter
class after cold-host restoration.

The separate provider roundtrip retains coverage of the durable row's full
identity, vitals, react state, action-bar payload, and spell state. A dead-pet
theory branch kills the pet before logout, verifies cold login leaves `PetGuid`
empty, then dispatches a registered synthetic Effect-109 spell and observes the
`SMSG_PET_SPELLS` packet, the same restored pet world identity, and the restored
50% health state. Focused verification reported 3 MockClient cases green
(alive cold host, dead cold host/effect 109, and durable row roundtrip).

Real build-5875 client acceptance and external-provider variants remain pending.
No production code is changed here.
