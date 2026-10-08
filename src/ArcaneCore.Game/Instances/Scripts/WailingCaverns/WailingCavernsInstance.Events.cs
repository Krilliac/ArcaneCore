using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>ScriptDev2 instance_wailing_caverns::OnCreatureCreate/DespawnAll and the Fanglord intro
/// (mangos-classic wailing_caverns/wailing_caverns.cpp:47-60,66-116,141-153).</summary>
public sealed partial class WailingCavernsInstance
{
    public const uint NpcDisciple = 3678, NpcNaralex = 3679;
    private readonly HashSet<ObjectGuid> _spawns = [];

    public override void Initialize()
    {
        base.Initialize();
        _spawns.Clear();
        Instance.FindUpdater<CreatureMapSystem>()?.RegisterEntryAi(NpcDisciple, c => new DiscipleOfNaralexAi(c, this));
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry is NpcDisciple or NpcNaralex)
        {
            StoreCreature(creature);
        }

        if (creature.Spawn is not null)
        {
            _spawns.Add(creature.Guid);
        }
    }

    public override void OnObjectCreate(GameObject go)
    {
        if (go.Entry == 180055)
        {
            StoreGameObject(go);
        }
    }

    public override void OnPlayerEnter(Player player)
    {
        if (QuestCompleteUnrewarded(player, 7944) && GetSingleGameObjectFromStorage(180055) is { } chest)
        {
            Instance.FindUpdater<GameObjectMapSystem>()?.ForceRespawn(chest);
        }
    }

    private void SpeakDiscipleIntro()
    {
        if (GetSingleCreatureFromStorage(NpcDisciple) is { } disciple)
        {
            Instance.FindUpdater<CreatureMapSystem>()?.SayText(disciple, 2101);
        }
    }

    internal Creature? Naralex => GetSingleCreatureFromStorage(NpcNaralex);

    internal void DespawnAll()
    {
        if (Instance.FindUpdater<CreatureMapSystem>() is not { } creatures)
        {
            return;
        }

        foreach (Creature creature in creatures.Creatures.Where(c => _spawns.Contains(c.Guid)).ToArray())
        {
            creatures.ForcedDespawn(creature, 0);
            creature.RespawnAtMs = creatures.ClockMs + 86_400_000; // instance_wailing_caverns::DespawnAll: 1 day
        }
    }
}
