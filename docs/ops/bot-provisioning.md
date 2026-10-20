# Provisioning bots and boosted characters

Both recipes use `.character boost` ([character boost](../areas/character-boost.md)) and need a running world. They change the live server, so
start with a dry run.

## Provision N bots at level L in zone Z

`tools/ops/provision-bots.ps1` creates N playerbots, logs them in, boosts each to level L and teleports them to game_tele places of the zone, then
reads the session log back and checks every bot. It logs a character in with `arcane-mock` as the operator account and sends the commands as chat.
The account is promoted to Administrator for the run and put back to its original level afterwards (a level that is not one of Player, Moderator,
GameMaster or Administrator is refused before anything changes).

```powershell
# What would run; touches nothing on the server.
pwsh -NoProfile -File tools/ops/provision-bots.ps1 -ProfileDirectory D:\arcane\profile -BinDirectory D:\arcane\bin `
    -Count 200 -Level 14 -TeleSpec 'Westfall=70,Goldshire=20,SentinelHill=10' -DryRun

# The real run (add -CreateAccount the first time if the OPSGM account does not exist yet).
pwsh -NoProfile -File tools/ops/provision-bots.ps1 -ProfileDirectory D:\arcane\profile -BinDirectory D:\arcane\bin `
    -Count 200 -Level 14 -TeleSpec 'Westfall=70,Goldshire=20,SentinelHill=10'
```

* `-TeleSpec` names are `game_tele` names exactly as `.tele name` takes them (for example `Westfall`, `Goldshire`, `SentinelHill`); the
  counts per place are exact (largest remainder). Choose the places of zone Z.
* Bot names are the prefix (`-NamePrefix`, default `Wfbot`) plus a letter suffix. Races and classes rotate over `-ClassSpec`.
* Exit code 0 means every bot is Running at the level; 5 means the list printed names bots that are not (a bot missing from the status output counts
  as failed). Pacing is 600 ms per line, so 200 bots take a few minutes. Create the file given by `-StopFile` to stop between two lines; bots
  already created stay (`.playerbot stop <Name>`).
* **`-Home`** anchors every bot to the place it was teleported to (`.playerbot home <Name> <place> <radius>`, after the teleports), so the brain
  keeps it in that area instead of following its quests across the map ([home area](../areas/playbots.md#home-area)). `-HomeRadius` is in yards,
  50 to 5000, default 600. The home is clipped to the zone of the place (a Sentinel Hill home does not reach into Duskwood); a place where the
  server's terrain knows no zone is refused, so no home is confirmed there. The run then also requires the server to have confirmed one `Playerbot home: N set` per bot, else the exit code is 5.
  A bot that ends up on another map is reported `off-map` in `.playerbot status` and is not moved by the bot; teleport it with `.tele name`.
* **Boost retry.** A boost the server refuses because the character is in combat, dead, settling a quest reward or not yet in the world is retried:
  up to `-BoostRetries` more passes (default 3), each after `-RetryWaitSeconds` (default 30), each boosting only the bots still not boosted. Bots refused as dead are revived (`.revive <Name>`) at the start of the retry pass, before their boost. Every pass
  is its own session with its own log (`provision-<stamp>-retry<N>.log` next to the main one) and the verdict reads all of them. A permanent refusal
  (a level above the character's, missing content) is never retried and fails the bot. The boost reply also reports the gray quests it dropped.
* **Status reply.** The final `.playerbot status` is followed by a wait of `-StatusWaitSeconds` (default 10): the status of 200 bots is many chat lines
  and the session closes shortly after the last command, so without the wait the check could read a truncated list and report bots missing. The
  runner waits for the `Playerbot status end: N` line the server prints after the last bot, so a bot that was never created still reaches the per-bot
  verification (exit 5). If that line does not arrive within `-StatusWaitSeconds`, the run fails with exit 1 (nothing verified).
* Re-running boosts again safely: levels, gear and bars are idempotent and experience is kept.

## Make a boosted character for an account via .pdump

Use this to give an account a ready-made character (the boost needs an online character, and a dump keeps one boosted template reusable).

1. Boost a template character: log it in, and run `.character boost 14` (or `.character boost <Name> 14` from a GM character).
2. Dump it: `.pdump write boosted14 <Name>`. The file is written inside the server's `pdump` directory (a bare name; paths are refused).
3. Load it into the account under a new name: `.pdump load boosted14 <ACCOUNT> <NewName>`. The account must exist and have a free character slot; the
   new character gets a new guid unless you give one.
4. Log the account in; the character is at level 14 with the kit, spells and bars. Run `.character boost <NewName> <higher level>` while it is online
   to raise it further.

The boost state that is saved with the character (spells, items, buttons, money) comes through the dump; a changed bar layout is shown after a relog
(see the in-session redraw limit in the area page).

## Run a bot siege (bots lead, you watch)

Two bot-led raids march a stored way and kill a creature, with no real player in either (details: [Siege](../areas/playbots-groups.md#siege-playerbot-siege)).
Do it in this order:

1. **Capacity.** Check there is room for the fleet (`.playerbot status`, the capacity lines of the provisioning runs above).
2. **Provision** the bots at the level of the content (the first section), enough for two to forty per raid.
3. **Station the Horde.** The Horde bots that hold Undercity are defenders with a post. For each post, stand at it and run
   `.playerbot send HordeDef* here` (the bots arrive) and then `.playerbot defend HordeDef* here 40` (`here` is your position; the radius is 10 to
   200 yards; more bots than the bulk cap need a trailing `force`; `.playerbot defend HordeDef* clear` ends the post). Use a different prefix per post.
   Example posts inside Undercity (map 0): the three lift tops at (1596, 292), (1544, 241) and (1595, 189); the entrance of the Royal Quarter; the
   Royal Quarter itself, near (1290, 316, -57). What a defender does (`PlayerbotBrain.Defend.cs`): it holds the post (it walks back when it is outside
   the radius, and stands, rests and buffs while nothing happens; a defender revived by the spirit healer waits out its resurrection sickness
   where it stands, status `decision=defend-wait-sickness`, and walks back when the aura ends); it engages a hostile player inside the radius, or one that attacks it, and helps a
   friendly creature that fights inside it; the defenders of one post heal each other. It turns its own PvP flag on (CMSG_TOGGLE_PVP) and keeps it
   while it has the post: a player may only be attacked while flagged (`CombatHooks.CanAttack`), so the Alliance bots can fight the defenders only
   once they are flagged, and a defender in turn fights only a player the same rule lets it attack (a flagged one; an Alliance bot flags itself on its
   first swing at a flagged Undercity guard). Defend is for the Horde side only: the siege attackers get no post, and a bot with a post is neither
   enlisted in a siege nor matched into a quest group. A post is memory only: a restart ends it.
4. **Stand at the points.** Starting outside Undercity, walk the way the raids should take (in, down through the lifts to the Royal Quarter) and
   `.playerbot siege waypoint add` at each point, in the order the raids take them; the last one is where the fight for the objective begins.
   `.playerbot siege waypoint list` checks them; `waypoint clear` starts over. Space the waypoints at least 60 yards apart: a lost fight retreats to
   the previous waypoint (to where the siege began from the first), and creatures follow a retreating raid for about 50 yards, so a closer point is
   fought at rather than left.
5. **Start.** Stand at the first waypoint, `.playerbot send Siegebot* here` puts the Alliance attackers there (or `send $tele`), then
   `.playerbot siege start Siegebot* #entry raids 2` (the entry is the creature's `creature_template` id, Sylvanas is 10181; `raids 1` keeps one raid).
6. **Watch.** With `.gm on` and `.gm visible off` you are invisible; `.playerbot siege watch on` sends you a line per raid as its order changes,
   `.playerbot siege status` shows the step, who lives and the orders (`retreat` while the raid walks back, `rally` once it stands at the point), `.playerbot siege status $leader` one raid and its last raid warnings.
   `.playerbot siege stop` ends it (the raids disband). Nothing is saved: a restart ends the siege and forgets the waypoints.
