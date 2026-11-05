# Real-client acceptance

Two statuses stay separate in this repository. An area is **implemented** when its automated tests pass. It is **client-accepted** only when someone has run it against an actual WoW 1.12.1
(build 5875) client and recorded the result. Automated tests, including the [mock client](../areas/mock-client.md), never make an area accepted. No area is client-accepted yet; the open item is
in the [roadmap](../ROADMAP.md#open-work).

This page is the shared checklist for such a run. Area pages and handoffs add their own checks: the [cluster run through the gateway](../CLUSTER_ACCEPTANCE.md), the
[quest reward run](../QUEST_REWARD_ACCEPTANCE.md), [quest client acceptance](../integration/quest-client-acceptance.md) and [quest UI acceptance](../integration/quest-ui-acceptance.md). Record the client build, the database
provider, the content and DBC source, the server revision, what you observed and the result. Do not commit screenshots or logs that hold account, character or address data.

## Setup

1. A disposable database (SQLite is fine), the realm and world daemons built and configured as in the [installation guide](installation.md), and a realm row whose address points at the world (or at the gateway for a split cluster).
2. The client's `realmlist.wtf` names the host that runs the realm daemon.
3. Accounts: two players, plus one staff account raised with `arcane-account set-gmlevel <user> gamemaster` before it logs in. Fresh characters make first-login behaviour visible.
   Auto-create on login (`Auth:AutocreateAccounts`) is **off** and not retail; either create accounts with `arcane-account` or switch it on yourself for a throwaway realm, never an internet-facing one.
4. Teleports exist now, so a second faction's character can be moved next to the first with GM commands instead of editing start rows.

## Checks, in order

Each step assumes the earlier ones still work.

**Logon.** The login screen reaches the realm list with no "Unable to connect", "Incorrect password" or "Unable to validate game version", and the realm is listed and selectable. A wrong password shows "Incorrect password" and
establishes no session. A banned or suspended account shows the matching client error.

**World handshake.** Selecting the realm clears the handshake without a disconnect (a header-cipher desync drops the connection at once) and reaches the character screen.

**Characters.** Create a character: it lists with the right name, race, class, appearance and zone. Enter the world: the character stands in the starting zone with the right model and no disconnect (the create-object block is the
most byte-sensitive packet). Delete a character: it leaves the list. Names `a1`, `x` and a 13-letter name are refused with the client's name error; `tHRALL` is created as `Thrall`.

**Movement and visibility.** Two clients in the same starting area see each other. Walk, jump, strafe and swim with each and watch the other client: movement is smooth in both directions with no rubber-banding. Walk about 100 yards
away and back: the other character disappears and reappears at its current position and keeps moving, with no frozen or duplicate model. Log one client out: its character disappears on the other.

**Persistence and sessions.** Log out and back in: the character is where it logged out. Stop the world daemon with Ctrl+C while a client is in the world, start it again and log in: the daemon reports it saved and stopped, and the
character reappears where it stood. Log the same account in a second time: the first copy is disconnected and the second enters. The world log shows no errors in any step.

**Session essentials.** The chat frame accepts input and the message of the day appears as system text. Tutorials close and stay closed across logins. A macro, a key binding, an action-bar slot and a second action bar survive a logout to the
character screen and a full client restart. `/logout` sits the character, roots it and runs a 20 second countdown; Cancel stands it up; letting it finish returns to the character screen without a disconnect; `/logout` in mid-air is refused.
Names show for nearby players, `/played` shows a time, and the in-game clock shows the server's time of day.

**Chat.** `/say` is seen next to the speaker and not about 35 yards away; `/yell` is seen there; `/e` is seen nearby. Targeted and untargeted text emotes show the right text. Whispers arrive with "To name:" on the sender, AFK and
DND auto-replies work and toggle off, and a whisper to an unknown name reports that no such player is playing. Across factions, `/say` arrives as gibberish, `/e` is not seen, a whisper is refused and `/who` lists only one's own faction. The
*Who* list shows level, class and zone and filters by part of a name.

**Staff commands.** `.gm on` sets the GM tag; `.gps` prints the position; `.announce` and `.notify` reach players; `.modify money` on a target changes its bags; `.kick` disconnects the target; `.gm chat on` puts the GM badge on `/say`.
A player typing `.gm on` gets "There is no such command.", and `.help` lists only the commands the account may use.

**Saved quest journal.** Prepare, on a disposable database, an incomplete ordinary quest with objective progress, a completed ordinary quest and a timed quest whose stored absolute deadline is about a minute away. At login the quest log
shows them with their counts and the matching text. Staying online until the timer ends fails the quest once and persists the cleared deadline; a relog keeps progress and completion and the timed quest stays failed. Repeat with the
deadline already past while offline: login completes in its normal order and the failure follows. A character with no quest rows has an empty log. Repeat on each database engine when assessing database acceptance.

**Quest interactions with a nearby questgiver.** With a supported neutral questgiver (a faction record that resolves, see [quests-npc](../areas/quests-npc.md#live-creature-interactions)): status shows, details open, acceptance fills a log slot,
abandonment clears it, a timed quest expires, and a relog restores each result.

## Evidence to capture

Screenshots of the realm list, character screen and world, the daemon log lines for the stage under test (challenge received, proof validated, realm list sent, auth digest validated, entered the world), and for a database check the account row with a
non-null salt and verifier. Keep them local; summarise them in the written result.
