using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Quests;
using ArcaneCore.World.Features;
using ArcaneCore.World.Npc;
using Microsoft.Extensions.DependencyInjection;

namespace ArcaneCore.World.GameObjects;

/// <summary>
/// The object scripts of mangos-classic world/go_scripts.cpp that ClassicDB binds to a gameobject_template ScriptName: go_andorhal_tower,
/// go_dragon_head, go_unadorned_spike, go_transpolyporter_bb and go_containment_coffer, plus the zone scripts go_rune_of_the_defiler
/// (blasted_lands.cpp), go_mausoleum_door and go_mausoleum_trigger (tirisfal_glades.cpp), go_lightning (azshara.cpp), go_covert_ops
/// (stonetalon_mountains.cpp), go_marshal_haggards_chest (elwynn_forest.cpp) and go_flagongut_fossil (wetlands.cpp). They run on the two continents (maps 0 and 1)
/// where those objects stand; the other go_scripts.cpp scripts are <see cref="WorldState.HolidayObjectsFeature"/> and
/// the elemental invasion (go_bells, go_darkmoon_faire_music, go_elemental_rift). Discovered <see cref="IWorldFeature"/>; see
/// docs/integration/world-scripts-scourge-go-20261010.md.
/// </summary>
public sealed class WorldGameObjectScriptsFeature(IServiceProvider services) : IWorldFeature
{
    private readonly HashSet<GameObjectMapSystem> _installed = [];
    private AndorhalTowerAi? _towers;
    private readonly DragonHeadAi _heads = new();
    private readonly UnadornedSpikeAi _spike = new();
    private readonly TranspolyporterAi _transpolyporter = new();
    private readonly ContainmentCofferAi _coffer = new();
    private readonly RuneOfTheDefilerAi _rune = new();
    private MausoleumDoorAi? _mausoleumDoor;
    private MausoleumTriggerAi? _mausoleumTrigger;
    private readonly LightningAi _lightning = new(Random.Shared);
    private readonly CovertOpsAi _covertOps = new();
    private readonly HaggardsChestAi _haggardsChest = new();
    private readonly FlagongutFossilAi _flagongutFossil = new();

    /// <summary>go_transpolyporter_bb's object (go_scripts.cpp:356): the Booty Bay trap casting Teleport to Gnomeregan 11362.</summary>
    public const uint TranspolyporterObject = 142172;

    /// <summary>go_unadorned_spike's object, Unadorned Stake (z2815 gameobject_template).</summary>
    public const uint UnadornedStakeObject = 175787;

    /// <summary>go_containment_coffer's object (go_scripts.cpp:555-558; z2815 gameobject_template).</summary>
    public const uint ContainmentCofferObject = 122088;

    /// <summary>go_rune_of_the_defiler's object (blasted_lands.cpp:58-60; z2815 gameobject_template): the Blasted Lands trap casting 12885.</summary>
    public const uint RuneOfTheDefilerObject = 153204;

    /// <summary>go_lightning's object, Theatric Lightning (azshara.cpp:518-521; z2815 gameobject_template).</summary>
    public const uint TheatricLightningObject = 183356;

    /// <summary>go_covert_ops's object, Venture Co. Wagon Trap (stonetalon_mountains.cpp:236-239; z2815 gameobject_template).</summary>
    public const uint VentureCoWagonTrapObject = 19590;

    public void Attach(WorldRuntime world)
    {
        ArgumentNullException.ThrowIfNull(world);
        _towers = new AndorhalTowerAi(QuestIncomplete, Credit);
        _mausoleumDoor = new MausoleumDoorAi(QuestIncomplete);
        _mausoleumTrigger = new MausoleumTriggerAi(QuestIncomplete);
        world.WorldTick += _ => Install(world);
        world.MapUnloading += map =>
        {
            if (map.FindUpdater<GameObjectMapSystem>() is { } objects) _installed.Remove(objects);
        };
    }

    private bool QuestIncomplete(Player player, uint questId)
        => services.GetService<QuestNpcFeature>()?.Services.StateOf(player)?.Quests.GetStatus(questId) == QuestStatus.Incomplete;

    private void Credit(Player player, uint creatureEntry)
        => services.GetService<QuestNpcFeature>()?.Services.KilledMonsterCredit(player, creatureEntry, default);

    private void Install(WorldRuntime world)
    {
        foreach (Map map in world.Maps.Where(m => m.MapId is 0 or 1))
        {
            if (map.FindUpdater<GameObjectMapSystem>() is not { } objects || !_installed.Add(objects)) continue;
            foreach (uint tower in AndorhalTowerAi.Credits.Keys) objects.RegisterAi(tower, _towers!);
            foreach (uint head in DragonHeadAi.Heralds.Keys) objects.RegisterAi(head, _heads);
            objects.RegisterAi(UnadornedStakeObject, _spike);
            objects.RegisterAi(TranspolyporterObject, _transpolyporter);
            objects.RegisterAi(ContainmentCofferObject, _coffer);
            objects.RegisterAi(RuneOfTheDefilerObject, _rune);
            objects.RegisterAi(MausoleumDoorAi.DoorObject, _mausoleumDoor!);
            objects.RegisterAi(MausoleumDoorAi.TriggerObject, _mausoleumTrigger!);
            objects.RegisterAi(TheatricLightningObject, _lightning);
            objects.RegisterAi(VentureCoWagonTrapObject, _covertOps);
            objects.RegisterAi(HaggardsChestAi.ChestObject, _haggardsChest);
            objects.RegisterAi(FlagongutFossilAi.FossilObject, _flagongutFossil);
        }
    }
}
