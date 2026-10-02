# M5 Acceptance Test — Runtime core

> Run with real WoW **1.12.1 (build 5875)** clients. M5 changes no gameplay; it must keep
> everything M1–M4 does working on the new world thread, and add persistence.

## Setup

1. Realm and world daemons configured per README (any engine; SQLite is fine for this test).
2. Two accounts, each with one character in Northshire (or any shared start zone).

## Procedure

1. Log in client A, enter the world. Log in client B, enter the world near A.
2. Walk, jump, strafe, swim (Northshire pond) with each; watch the other client.
3. Walk A more than ~100 yards away and back (M4 step 5).
4. With A, log out (or ALT-F4). Wait 2 seconds, log back in.
5. Stop the world daemon (Ctrl+C) while B is in the world, start it again, log B in.
6. While A is in the world, start client A again on the same account (a second copy) and
   log in with the same character.

## Expected result (PASS criteria)

- Steps 1–3 behave exactly as the M4 acceptance (smooth movement both ways, disappear and
  reappear at the range boundary, no disconnects). Jumping and swimming render on the other
  client.
- Step 4: A re-enters at the spot where it logged out, not at the start position; B sees A
  vanish and reappear.
- Step 5: the daemon shuts down cleanly ("World saved and stopped"); B reappears where it
  was standing when the daemon stopped.
- Step 6: the first copy of client A is disconnected; the second enters the world normally.
- The world daemon log shows no errors during any step.

Steps 1–6 all passing = **M5 PASS**.

## Reference basis

`MILESTONE_M5.md` lists every protocol detail and the reference that confirmed it.
