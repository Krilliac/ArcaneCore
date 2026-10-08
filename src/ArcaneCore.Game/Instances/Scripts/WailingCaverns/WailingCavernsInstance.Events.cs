using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>ScriptDev2 instance_wailing_caverns::OnCreatureCreate and the Fanglord intro
/// (mangos-classic wailing_caverns/wailing_caverns.cpp:47-60,66-116). The source's <c>m_spawns</c> list and DespawnAll (:141-151) are not
/// ported: their only caller, step 13 of the disciple's chamber event, is never reached (see <see cref="DiscipleOfNaralexAi"/>).</summary>
public sealed partial class WailingCavernsInstance
{
    public const uint NpcDisciple = 3678, NpcNaralex = 3679;

    public override void Initialize()
    {
        base.Initialize();
        Instance.FindUpdater<CreatureMapSystem>()?.RegisterEntryAi(NpcDisciple, c => new DiscipleOfNaralexAi(c, this));
    }

    public override void OnCreatureCreate(Creature creature)
    {
        if (creature.Entry is NpcDisciple or NpcNaralex)
        {
            StoreCreature(creature);
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
}
