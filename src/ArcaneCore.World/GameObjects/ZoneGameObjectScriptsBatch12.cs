using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// go_marshal_haggards_chest (mangos-classic eastern_kingdoms/elwynn_forest.cpp:36-56, gameobject 1562): when the chest is looted out, a
/// Forlorn Spirit (2044) rises at the farm and attacks the looter unless a living one is within 20 yd of the chest. It stays five minutes out
/// of combat. Runs on <see cref="IGameObjectAi.OnLootedOut"/>, the loot-release side of the source's OnLootStateChange(GO_JUST_DEACTIVATED).
/// </summary>
internal sealed class HaggardsChestAi : IGameObjectAi
{
    public const uint ChestObject = 1562, NpcForlornSpirit = 2044, SpiritLifetimeMs = 300_000;
    public const float SearchRange = 20f;
    public static readonly (float X, float Y, float Z, float O) SpiritSpawn = (-9553.5127f, -1430.542f, 62.377f, 4.6077f);

    public bool OnTrapTarget(GameObjectMapSystem objects, GameObject go, Unit target) => false;

    public void Update(GameObjectMapSystem objects, GameObject go, uint diffMs)
    {
    }

    public void OnLootedOut(GameObjectMapSystem objects, GameObject go, Player? looter)
    {
        if (objects.Map.FindUpdater<CreatureMapSystem>() is not { } creatures
            || creatures.ClosestOfEntry(go, NpcForlornSpirit, SearchRange, aliveOnly: true) is not null)
        {
            return;
        }

        if (creatures.SummonDeadDespawnAt(NpcForlornSpirit, SpiritSpawn.X, SpiritSpawn.Y, SpiritSpawn.Z, SpiritSpawn.O) is { } spirit)
        {
            creatures.MarkTimedOutOfCombatDespawn(spirit, SpiritLifetimeMs);
            if (looter is not null)
            {
                spirit.AI?.AttackStart(looter);
            }
        }
    }
}

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
