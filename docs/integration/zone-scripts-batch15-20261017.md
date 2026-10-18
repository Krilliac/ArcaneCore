# Zone scripts, batch 15 (2026-10-17)

Ports mangos-classic ScriptDev2 `npc_theramore_spar_controller` (kalimdor/dustwallow_marsh.cpp:796-1221), bound to Master Szigeti (5090).
It wasn't in ArcaneCore before. The script lives in `Creatures/Scripts/TheramoreSparControllerAI.cs` and is registered in `CreatureAiServices`.

## Behaviour

Theramore's sparring rounds work as follows:
- The controller gathers the three blue guards and three red guards within 20 yd, puts them on faction 151 and finds both medics.
- The first pair is announced by Criton and both captains, walks to its corners and salutes. Criton then introduces the bout and starts it.
- Each fighter fights on its team faction (1621/1622) until combat stops.
- When one fighter falls to 30% health, the other wins. Both stop fighting and get their faction back, and the medics cast First Aid.
- After a bout, Szigeti points and names the winning team. The winner's teammates cheer, the losing captain names the loser, and Criton
  sends the fighters to their corners.
- The next member of the losing team steps up. When the losing team has no one left, Criton names the overall winner, and 8 s later the
  captains say their motivation lines.
- After that, everyone is gathered again and a new round starts 20 s later.

## Deviations

- Szigeti is the controller itself. When the 15 yd search finds no other Szigeti, the controller speaks itself.
- If a whole team is missing, the source would read an empty list. Here no bout starts; the controller gathers again every 20 s.
- The source's `HandleBlueWin` reads `active[BLUE]`. That is read as the blue team's fighter.
- React states use the creature's own `ReactState` (passive while walking to the corners or home, defensive while fighting).

## Not ported (checked in this batch)

- **`go_omen_cluster` (moonglade.cpp):** Omen is already handled by `Holidays/LunarFestivalScripts.cs` `OmenController`, which follows the
  vmangos firework count. Porting the cmangos rocket counter as well would count launches twice.
- **`event_wildhammer_message` (hinterlands.cpp, event 8608):** needs an Eastern Kingdoms world map script that stores Falstad Wildhammer.
  ArcaneCore has none yet. Its yell (-27) also has no entry in the downloaded scriptdev2 SQL.
- **`npc_cork_gizelton` / `npc_rigger_gizelton` (desolace.cpp):** a long caravan escort, left for a later batch.
