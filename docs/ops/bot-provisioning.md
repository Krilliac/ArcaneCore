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
