using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// The Alterac Valley assault scripts of one match (vmangos AddSC_bg_alterac, scripts/battlegrounds/battleground_alterac.cpp:4894-5011, the
/// assault part): which creature of the match's map runs which script, the beacon object script, and the creature groups the scripts form.
/// classic-db carries no script names, so the scripts go by entry, as the vmangos script names of these entries do:
/// <list type="bullet">
/// <item>npc_AV_blood_collector (<see cref="AvEventAI"/>): the quartermasters, the six wing commanders, Primalist Thurloga, Arch Druid
/// Renferal and the two cavalry commanders. Murgot Deepforge and Regzar keep their own AI (their menu is <see cref="AlteracValley.QuartermasterMenu"/>).</item>
/// <item>npc_AV_troops_chief (<see cref="AvTroopsChiefAI"/>): Field Marshal Teravaine, Warmaster Garrick and the eight ground troops.</item>
/// <item>npc_cavalry (<see cref="AvCavalryAI"/>): the wolf and ram riders.</item>
/// <item>av_warrider (<see cref="AvWarRiderAI"/>): the six commanders' war riders and gryphons.</item>
/// <item>npc_worldboss_h_av / _a_av (<see cref="AvWorldBossAI"/>): Lokholar the Ice Lord and Ivus the Forest Lord.</item>
/// <item>npc_frostwolf_shaman / npc_druid_of_the_grove (<see cref="AvSummonerAddAI"/>).</item>
/// <item>go_av_beacon (<see cref="AvBeaconAi"/>): the six beacons.</item>
/// </list>
/// World thread; one instance per match map.
/// </summary>
public sealed class AlteracValleyScripts
{
    private static readonly uint[] s_eventEntries =
    [
        AlteracValley.NpcQuartermasterHorde, AlteracValley.NpcQuartermasterAlliance,
        AlteracValley.NpcWingCommanderGuse, AlteracValley.NpcWingCommanderJeztor, AlteracValley.NpcWingCommanderMulverick,
        AlteracValley.NpcWingCommanderSlidore, AlteracValley.NpcWingCommanderIchman, AlteracValley.NpcWingCommanderVipore,
        AlteracValley.NpcPrimalistThurloga, AlteracValley.NpcArchDruidRenferal,
        AlteracValley.NpcWolfRiderCommander, AlteracValley.NpcRamRiderCommander,
    ];

    private static readonly uint[] s_troopsChiefEntries =
    [
        AlteracValley.NpcFieldMarshalTeravaine, AlteracValley.NpcWarmasterGarrick,
        AlteracValley.NpcFrostwolfReaver, AlteracValley.NpcSeasonedReaver, AlteracValley.NpcVeteranReaver, AlteracValley.NpcChampionReaver,
        AlteracValley.NpcStormpikeCommando, AlteracValley.NpcSeasonedCommando, AlteracValley.NpcVeteranCommando, AlteracValley.NpcChampionCommando,
    ];

    private static readonly uint[] s_cavalryEntries = [AlteracValley.NpcWolfRider, AlteracValley.NpcRamRider];

    private static readonly uint[] s_warRiderEntries =
    [
        AlteracValley.NpcWarRiderGuse, AlteracValley.NpcWarRiderJeztor, AlteracValley.NpcWarRiderMulverick,
        AlteracValley.NpcGryphonSlidore, AlteracValley.NpcGryphonIchman, AlteracValley.NpcGryphonVipore,
    ];

    private static readonly uint[] s_beaconEntries =
    [
        AlteracValley.GameObjectBeaconGuse, AlteracValley.GameObjectBeaconJeztor, AlteracValley.GameObjectBeaconMulverick,
        AlteracValley.GameObjectBeaconSlidore, AlteracValley.GameObjectBeaconIchman, AlteracValley.GameObjectBeaconVipore,
    ];

    private readonly Dictionary<ObjectGuid, (ObjectGuid Leader, float Angle, float Distance)> _formation = [];
    private readonly AvBeaconAi _beacons;
    private CreatureMapSystem? _creatures;
    private GameObjectMapSystem? _objects;

    public AlteracValleyScripts(AlteracValley match)
    {
        ArgumentNullException.ThrowIfNull(match);
        Match = match;
        _beacons = new AvBeaconAi();
    }

    public AlteracValley Match { get; }

    /// <summary>Every creature entry a script of this class drives.</summary>
    public static IEnumerable<uint> CreatureEntries
        => [.. s_eventEntries, .. s_troopsChiefEntries, .. s_cavalryEntries, .. s_warRiderEntries, AlteracValley.NpcLokholar, AlteracValley.NpcIvus,
            AlteracValley.NpcFrostwolfShaman, AlteracValley.NpcDruidOfTheGrove];

    /// <summary>Every object entry a script of this class drives.</summary>
    public static IReadOnlyList<uint> GameObjectEntries => s_beaconEntries;

    /// <summary>Give the match map's creatures their scripts (idempotent per system).</summary>
    public void Attach(CreatureMapSystem creatures)
    {
        ArgumentNullException.ThrowIfNull(creatures);
        if (ReferenceEquals(_creatures, creatures))
        {
            return;
        }

        _creatures = creatures;
        foreach (uint entry in s_eventEntries)
        {
            creatures.RegisterEntryAi(entry, c => new AvEventAI(c, this));
        }

        foreach (uint entry in s_troopsChiefEntries)
        {
            creatures.RegisterEntryAi(entry, c => new AvTroopsChiefAI(c, this));
        }

        foreach (uint entry in s_cavalryEntries)
        {
            creatures.RegisterEntryAi(entry, c => new AvCavalryAI(c, this));
        }

        foreach (uint entry in s_warRiderEntries)
        {
            creatures.RegisterEntryAi(entry, c => new AvWarRiderAI(c, this));
        }

        creatures.RegisterEntryAi(AlteracValley.NpcLokholar, c => new AvWorldBossAI(c, this, horde: true));
        creatures.RegisterEntryAi(AlteracValley.NpcIvus, c => new AvWorldBossAI(c, this, horde: false));
        creatures.RegisterEntryAi(AlteracValley.NpcFrostwolfShaman, c => new AvSummonerAddAI(c, this));
        creatures.RegisterEntryAi(AlteracValley.NpcDruidOfTheGrove, c => new AvSummonerAddAI(c, this));
    }

    /// <summary>Run the beacon script on the match map's objects (idempotent per system).</summary>
    public void Attach(GameObjectMapSystem objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        if (ReferenceEquals(_objects, objects))
        {
            return;
        }

        _objects = objects;
        foreach (uint entry in s_beaconEntries)
        {
            objects.RegisterAi(entry, _beacons);
        }
    }

    /// <summary>The match is over or its map unloads: the creatures and objects go back to their own AI.</summary>
    public void Detach()
    {
        if (_creatures is { } creatures)
        {
            foreach (uint entry in CreatureEntries)
            {
                creatures.UnregisterEntryAi(entry);
            }
        }

        if (_objects is { } objects)
        {
            foreach (uint entry in s_beaconEntries)
            {
                objects.UnregisterAi(entry);
            }
        }

        _creatures = null;
        _objects = null;
        _formation.Clear();
    }

    // ------------------------------------------------------------------ creature groups

    /// <summary>
    /// vmangos Creature::JoinCreatureGroup(leader, dist, angle, OPTION_FORMATION_MOVE | OPTION_AGGRO_TOGETHER | OPTION_EVADE_TOGETHER):
    /// the member keeps its place beside the leader (it follows at that distance and angle from the leader's facing), and fights and stops
    /// fighting with the group. The rallies take the slot from <see cref="AvScript.FormationSlot"/>.
    /// </summary>
    internal void JoinGroup(Creature member, Creature leader, float angle, float distance)
    {
        _formation[member.Guid] = (leader.Guid, angle, distance);
        FollowLeader(member);
    }

    /// <summary>The member's leader while both are in the map and the leader lives.</summary>
    internal Creature? LeaderOf(Creature member)
        => _formation.TryGetValue(member.Guid, out var link) && member.System?.FindCreature(link.Leader) is { IsAlive: true } leader ? leader : null;

    /// <summary>The living members of a leader's group.</summary>
    internal IEnumerable<Creature> MembersOf(Creature leader)
    {
        foreach ((ObjectGuid member, (ObjectGuid Leader, float, float) link) in _formation.ToArray())
        {
            if (link.Leader == leader.Guid && leader.System?.FindCreature(member) is { IsAlive: true } creature)
            {
                yield return creature;
            }
        }
    }

    /// <summary>The member leaves its group (its leader died or it goes on its own).</summary>
    internal void LeaveGroup(Creature member) => _formation.Remove(member.Guid);

    /// <summary>A member takes its place in the formation again (after a fight).</summary>
    internal bool FollowLeader(Creature member)
    {
        if (!_formation.TryGetValue(member.Guid, out var link) || LeaderOf(member) is not { } leader)
        {
            return false;
        }

        member.Motion.Clear();
        member.Motion.MoveFollow(leader, link.Distance, link.Angle);
        return true;
    }

    /// <summary>OPTION_AGGRO_TOGETHER: whoever of the group enters a fight, the others who are not fighting attack the same enemy.</summary>
    internal void GroupAggro(Creature member, Unit enemy)
    {
        Creature? leader = _formation.ContainsKey(member.Guid) ? LeaderOf(member) : member;
        if (leader is null)
        {
            return;
        }

        foreach (Creature other in MembersOf(leader).Append(leader))
        {
            if (!ReferenceEquals(other, member) && other.IsAlive && !other.Combat.IsInCombat && other.AI is { } ai)
            {
                ai.AttackStart(enemy);
            }
        }
    }

    /// <summary>The creatures of the match map (for the scripts' searches).</summary>
    internal CreatureMapSystem? Creatures => _creatures;
}
