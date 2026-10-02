# M6 Acceptance Test — Session essentials, chat, GM commands

> Run with real WoW **1.12.1 (build 5875)** clients. Everything from the M5 acceptance must
> keep working.

## Setup

1. Realm and world daemons configured per README (any engine).
2. Accounts: **A** and **B** (players), **G** (staff):
   `arcane-account set-gmlevel G gamemaster` before G logs in.
3. Characters: A and G human, B one human *and* one orc (B switches between them), all on
   fresh characters so first-login behaviour is visible.
4. Teleports arrive in M7, so for step 8 create the orc where the humans start: before
   creating it, copy the human warrior's `MapId`, `ZoneId`, `X`, `Y`, `Z` into the orc
   warrior's row (`Race` 2, `Class` 1) of `player_create_info` in the world database.

## Procedure

1. **Login.** Log A in. The chat frame accepts input; the message of the day appears as
   system text; tutorial pop-ups appear (fresh account). Close a tutorial (or turn
   tutorials off in the interface options), log out to the character screen and log in
   again: the closed tutorial (or every tutorial) does not come back.
2. **Settings survive.** With A: create a macro, rebind a key, drag *Attack* onto action
   slot 1, enable a second action bar. Log out to the character screen (countdown), log in
   again, then quit the client completely and log in again. Everything is still there.
3. **Logout.** Type `/logout`: the character sits, cannot move, a 20-second countdown
   runs; press *Cancel*: it stands up and can move. `/logout` again and let it run: back
   at the character screen without a disconnect. Jump and `/logout` in mid-air: refused.
4. **Names and time.** A and B (human) stand together: each sees the other's name. `/played`
   shows a played time. The in-game clock shows the server's time of day.
5. **Chat in range.** B stands next to A. A `/say`s: B sees it. B walks ~35 yards away;
   A `/say`s: B does not see it; A `/yell`s: B sees it. A `/e waves happily`: B sees it.
6. **Text emotes.** A targets B and types `/wave`: both clients show the "waves at B"
   text; without a target the untargeted text. (The wave *animation* for text emotes needs
   EmotesText.dbc and arrives with M8.)
7. **Whispers.** A whispers B (`/w B hi`): B receives it, A sees "To B: hi". B types
   `/afk lunch`; A whispers again: A gets B's AFK reply "lunch". B types `/dnd`; A whispers:
   DND reply; B types `/dnd` again: no more replies. A whispers a name that does not exist:
   the client reports that no such player is playing.
8. **Factions.** B logs in the orc near A. A `/say`s: the orc sees it in gibberish
   (Common). A `/e`s: the orc does not see it. A whispers the orc: refused (wrong faction).
   `/who` on each side lists only its own faction.
9. **/who.** A opens the *Who* list: A and the other humans online appear with level,
   class and zone; filtering by part of a name works.
10. **Character names.** On the character screen try `a1`, `x` and a 13-letter name:
    each is refused with the client's name error; `tHRALL` is created as `Thrall`.
11. **Staff (G).** `.gm on`: G gets the GM tag and "GM mode is ON." on screen; `.gps`
    prints the position; `.announce Hello` and `.notify Hello` reach A and B;
    target A and `.modify money 10000`: A's bags show 1 gold; `.kick B` disconnects B;
    `.gm chat on` then `/say`: A sees G's line with the GM badge. A typing `.gm on`
    gets "There is no such command."; `.help` shows A only its own commands.

## Expected result (PASS criteria)

- Every step behaves as described, with no disconnects except the `.kick`.
- The world daemon log shows no errors.

Steps 1–11 all passing = **M6 PASS**.

## Reference basis

`MILESTONE_M6.md` lists every protocol detail and the reference that confirmed it.
