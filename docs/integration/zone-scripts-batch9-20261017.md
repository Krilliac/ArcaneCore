# Zone scripts batch 9: The Stave of the Ancients (2026-10-17)

Ports of the mangos-classic ScriptDev2 demons from the hunter quest "The Stave of the Ancients" (7636), plus The Cleaner.

| Friendly / demon | Source | Script |
| --- | --- | --- |
| Franklin the Friendly 14529 / Klinfran the Crazed 14534 | eastern_kingdoms/burning_steppes.cpp `npc_klinfran` | `KlinfranAI` |
| Nelson the Nice 14536 / Solenor the Slayer 14530 | kalimdor/silithus.cpp `npc_solenor` | `SolenorAI` |
| Artorius the Amiable 14531 / Artorius the Doombringer 14535 | kalimdor/winterspring.cpp `npc_artorius` | `ArtoriusAI` |
| The Cleaner 14503 | world/npcs_special.cpp `npc_the_cleaner` | `TheCleanerAI` |

All the shared rules live in `StaveDemonAI` (src/ArcaneCore.Game/Creatures/Scripts/StaveOfTheAncientsAI.cs):

- Gossip (`StaveDemonGossip`, registered for 14529, 14536 and 14531 in QuestNpcFeature) offers "Show me your real face, demon." while 7636 is
  incomplete. Choosing it stores the player, stops the NPC and clears its NPC flags. The friendly form emotes 5 s later (point, laugh or roar)
  and takes the demon entry (UpdateEntry) after 10 s.
- The demon fights a single hunter. A non-hunter attacker, or a second unit on its threat list, makes it vanish with a 15-minute respawn and
  summons The Cleaner (dead-despawn) to attack every living unit that was on the threat list. Left alone for 20 minutes out of combat, it
  vanishes without The Cleaner. Its death sets a 2-hour respawn.
- Klinfran: Demonic Frenzy 23257 every 15 s (first after 5 s), with emote -1000001. Scorpid Sting rank 4 (14277) removes the frenzy and
  casts Entropic Sting 23260.
- Solenor: Soul Flame 23272 as he appears, then again 150 ms later. Creeping Doom 23589 every 15 s (first after 3-6 s); its summons attack his
  victim. Dreadful Fright 23275 on a victim more than 5 yd away, every 15-20 s (first after 10-15 s). Frost Trap aura 13810 removes Soul Flame.
  Wing Clip rank 3 (14268) casts Crippling Clip 23279, with emote -1001252. His guardians despawn when he evades or vanishes.
- Artorius: Demonic Frenzy every 15-20 s (first after 5-8 s). Demonic Doom 23298 is tried every 7.5 s on a victim within 25 yd. Serpent Sting
  rank 8 or 9 (13555, 25295) casts Stinging Trauma 23299, with emote -1001251.
- The Cleaner: Immunity 29230 on every reset, an aggro line (-1001253), despawns on evade, and from 3 s on despawns once its threat list is empty.

Not ported from the friendly forms: cmangos's 35-minute respawn on reset, the NearTeleport home, and the re-initialised waypoint movement. The
spawn's own respawn and movement data cover those here.

Not ported yet: Simone the Inconspicuous 14527 / Simone the Seductress 14533 with Precious 14528 / Precious the Devourer 14538 (Un'Goro).
These need the pet link (Precious follows Simone and transforms with her), and that is the next batch.

Tests: tests/ArcaneCore.Game.Tests/CreatureAi/ZoneScriptBatch9Tests.cs (22).
