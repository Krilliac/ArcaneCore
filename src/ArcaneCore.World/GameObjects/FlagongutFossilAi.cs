using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_flagongut_fossil (mangos-classic eastern_kingdoms/wetlands.cpp:231-253, gameobject 9630): when the fossil is looted out, Prospector
/// Whelgar (1077) within 10 yd talks and says broadcast text 1235.
/// </summary>
internal sealed class FlagongutFossilAi : IGameObjectAi
{
    public const uint FossilObject = 9630, NpcProspectorWhelgar = 1077, EmoteOneshotTalk = 1;
    public const int SayItemLooted = 1235;
    public const float SearchRange = 10f;

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public void OnLootedOut(GameObjectMapSystem objects, GameObject go, Player? looter)
    {
        if (objects.Map.FindUpdater<CreatureMapSystem>() is { } creatures
            && creatures.ClosestOfEntry(go, NpcProspectorWhelgar, SearchRange, aliveOnly: true) is { } whelgar)
        {
            creatures.PlayEmote(whelgar, EmoteOneshotTalk);
            creatures.SayText(whelgar, SayItemLooted);
        }
    }
}
