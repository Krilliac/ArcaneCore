# Zone scripts batch 10: Simone and Precious (2026-10-17)

This batch ports the last Stave of the Ancients (7636) demon, from mangos-classic ScriptDev2 kalimdor/ungoro_crater.cpp.

| Creature | Source | Script |
| --- | --- | --- |
| Simone the Inconspicuous 14527 | `npc_simone_the_inconspicuous` | `SimoneTheInconspicuousAI` (src/ArcaneCore.Game/Creatures/Scripts/SimoneAI.cs) |
| Simone the Seductress 14533 | `npc_simone_seductress` | `SimoneTheSeductressAI` |
| Precious the Devourer 14538 | `npc_precious_the_devourer` | `PreciousTheDevourerAI` |

Behaviour:

- Simone's gossip is the shared `StaveDemonGossip`. It now opens any `IStaveFriendlyForm`, and is registered for 14527 as well.
- On every reset, Simone takes her gossip back, and Precious (14528) follows her. A new Precious is summoned when none is within 100 yd.
  - Deviation: this lookup runs on her first update after the reset, not inside it. At the first spawn, a database Precious may be added
    to the map after her, and looking at once would summon a second one.
- Choosing the gossip line makes her laugh, stop and lose her gossip. She shouts 5 s later. At 10 s, if the player is still on the map:
  - Simone the Seductress and Precious the Devourer are summoned. The Seductress knows the friendly Simone and her Devourer; the Devourer
    knows the Seductress.
  - Precious and Simone are despawned. Simone's respawn is set to 35 minutes.
- Simone the Seductress follows the one-hunter rules of the other Stave demons.
  - Pulling either the Seductress or the Devourer pulls the other.
  - A second unit on her threat list (checked at once) or on Precious's (checked every 2 s) makes both vanish. The Cleaner then attacks
    everyone on both threat lists.
  - Her vanishing moves the despawned friendly Simone's respawn to 15 minutes, her death to 2 hours. This uses the new
    `CreatureMapSystem.SetRespawnIn`: cmangos SetRespawnDelay + SetRespawnTime + SaveRespawnTime on a corpse.
  - Out of combat, she sends an engaged Precious home every 2.5 s, the first check after 7.5 s. When she gets home and her Devourer is dead
    or gone, she summons a new one.
  - Spells: Temptress' Kiss 23205 (first at 3-6 s, then every 45 s) and Chain Lightning 23206 (first at 3-6 s, then every 8-12 s). Viper
    Sting rank 3 (14280) silences her (23207) with emote -1001250. Left alone for 20 minutes out of combat, she vanishes without The Cleaner.
- Precious the Devourer: its aggro pulls the Seductress in. It despawns when it evades with her dead. Out of combat, it sends an engaged
  Seductress home every 2.5 s.

Tests: tests/ArcaneCore.Game.Tests/CreatureAi/ZoneScriptBatch10Tests.cs (13).
