# Scourge invasion choreography, part 4 (2026-10-10)

Stacked on #41 (`grok/scourge-invasion-3`). Reference: mangos-classic `scourge_invasion.cpp` (npc_cultist_engineer, SummonCultists,
SummonBoss, ScourgeMinion / Shadow of Doom, IsGuardOrBoss).

## Delivered
- **Buttress (damaged shard)**: first after 5 s, then hourly (the reference's placeholder for 27888): shard to full health, out-of-combat
  Shadows of Doom within 200 yd despawn, old cultists and Summoner Shields within 28 yd go, four Cultist Engineers (16230, 1 h) are placed
  on the circle's 6.95 x 6.75 ellipse facing in. `ScourgeButtress.Run`.
- **Cultist Engineer** (`CultistEngineerAi`): passive; raises a Summoner Shield (181142, 1 h, at its feet), spawn-in visual, channels
  Buttress (28078) into its shard after 1 s. On death the shard takes Damage Crystal (28041) and the cultist's shield is removed.
- **8-rune gossip** (`CultistEngineerGossip`, installed with `AddGossipScript`): npc text 8436, option BCT 12112. Selecting it with eight
  Necrotic Runes (22484) destroys the runes, summons a Shadow of Doom (16143, 1 h, OOC-or-dead), and the cultist dies (Quiet Suicide).
  Without eight runes nothing happens and the menu closes.
- **Shadow of Doom** (`ShadowOfDoomAi`): arrives immune to players, says one of BCT 12420/12421/12422/12243 to its summoner, smoke visual;
  5 s later it drops the immunity and attacks the summoner. Mind Flay every 6.5-13 s, Fear every 14.5 s (first 2 s). Death zaps the shard
  (28056).
- **Guards**: Pallid Horror / Patchwork Terror and Flameshockers call capital defenders (IsGuardOrBoss list incl. Bolvar, Sylvanas,
  Varimathras) within 25 yd that have no victim to attack them.

## Tests
`ScourgeInvasionChoreographyTests`: buttress placement/shields/channel/replace-not-stack; rune gossip (0, 7 and 10 runes), Shadow summon,
immunity drop and attack; guards in range join, out of range and non-guards do not.

## Limits
- The shield is placed directly rather than through 28132's summon-object effect; the item-count condition on the gossip line is not
  shown/hidden (the reference's DB gossip handles that) — the option is always listed and checks runes on select.
- The guard call does not test line of sight (the reference's IsWithinLOSInMap).
