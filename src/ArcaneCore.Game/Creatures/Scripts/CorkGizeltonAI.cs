using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Cork Gizelton (entry 11625, Desolace), the head of the Gizelton caravan: mangos-classic ScriptDev2 <c>npc_cork_gizelton</c>
/// (kalimdor/desolace.cpp:418-567, registered :695-699) on <see cref="EscortAI"/>. The caravan (spawn group 19019: Cork, Rigger Gizelton and two
/// Caravan Kodos) loops the zone on waypoint_path 19019 in a formation that relay 1162501 creates, and stops at the Kolkar and the Kodo
/// Graveyard camps to offer its two escort quests. The data does the rest of the story: the movement scripts of the two escort paths
/// (dbscripts_on_creature_movement 1162501-1162504 and 1162601-1162604) say the warnings, summon the ambushers, put the caravan back to its
/// own factions and send it on at a run; the quest start scripts give the other Gizelton the escort faction.
/// <list type="bullet">
/// <item>"Bodyguard for Hire" (5821, from Cork): Cork walks waypoint_path 11625 and at point 19 gives the group the quest event;</item>
/// <item>"Gizelton Caravan" (5943, from Rigger, <see cref="RiggerGizeltonAI"/>): Cork walks waypoint_path 11626 for the player who took it,
/// and at point 21 gives the group the quest event.</item>
/// </list>
/// After either escort the caravan goes on with its loop at a run, from point 33 (north) or 100 (south) of waypoint_path 19019.
/// When a caravan member dies during an escort the quest fails for the group, either 60 s later or once nobody of the caravan is still
/// fighting, and the whole caravan disappears (the spawn group brings it back).
/// </summary>
public sealed class CorkGizeltonAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 11625, NpcRiggerGizelton = 11626, NpcCaravanKodo = 11564;
    public const uint QuestBodyguardForHire = 5821, QuestGizeltonCaravan = 5943;
    public const uint CaravanPathId = 19019, PathCork = 11625, PathRigger = 11626;
    public const uint CaravanPointAfterNorthEscort = 33, CaravanPointAfterSouthEscort = 100;
    public const uint NorthCreditPoint = 19, SouthCreditPoint = 21;
    public const uint FailTimerMs = 60_000;

    /// <summary>How far from Cork the caravan members are looked for when an escort starts (the formation keeps them within a few yards).</summary>
    public const float CaravanRange = 40f;

    private readonly List<Creature> _caravan = [];
    private uint _questId;
    private int _failMs = (int)FailTimerMs;
    private uint _resumeAtPoint;

    /// <summary>StatusFailed: a caravan member died during the escort.</summary>
    public bool StatusFailed { get; private set; }

    /// <summary>ScriptDone: the failure was handled.</summary>
    public bool ScriptDone { get; private set; }

    /// <summary>SouthQuest: the escort is Rigger's "Gizelton Caravan".</summary>
    public bool SouthQuest { get; private set; }

    /// <summary>The members Cork watches during an escort.</summary>
    public IReadOnlyList<Creature> Caravan => _caravan;

    /// <summary>QuestAccept_npc_cork_gizelton (:558-566).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestBodyguardForHire)
        {
            StartCaravanEscort(player, questId, PathCork, south: false);
        }
    }

    /// <summary>StartRiggerEscort (:545-549), from <see cref="RiggerGizeltonAI"/>: Cork leads the escort that Rigger's quest asked for.</summary>
    public bool StartRiggerEscort(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return StartCaravanEscort(player, QuestGizeltonCaravan, PathRigger, south: true);
    }

    // The source passes m_questForEscort (the last quest Cork's own Start was given) to the Rigger escort; here the escort carries 5943 itself,
    // so Cork's death or a lost player fails the quest that was taken.
    private bool StartCaravanEscort(Player player, uint questId, uint path, bool south)
    {
        if (!Start(run: false, player: player, questId: questId, waypointPath: path))
        {
            return false;
        }

        SouthQuest = south;
        _questId = questId;
        StatusFailed = false;
        ScriptDone = false;
        _failMs = (int)FailTimerMs;
        _resumeAtPoint = 0;
        _caravan.Clear();
        if (System is { } system)
        {
            _caravan.AddRange(system.Creatures.Where(c => !ReferenceEquals(c, Me) && c.IsAlive
                && c.Entry is NpcRiggerGizelton or NpcCaravanKodo && Within(c, CaravanRange)));
        }

        return true;
    }

    protected override void Reset()
    {
    }

    protected override void JustRespawned()
    {
        base.JustRespawned();
        _caravan.Clear();
        _questId = 0;
        SouthQuest = false;
        StatusFailed = false;
        ScriptDone = false;
        _resumeAtPoint = 0;
        _failMs = (int)FailTimerMs;
    }

    /// <summary>WaypointReached (:507-543): the credit point of the escort being led.</summary>
    protected override void WaypointReached(uint pointId)
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            return;
        }

        Player? player = GetPlayerForEscort();
        switch (pointId)
        {
            case NorthCreditPoint when !SouthQuest:
                Credit(player, QuestBodyguardForHire, CaravanPointAfterNorthEscort);
                break;
            case SouthCreditPoint when SouthQuest:
                Credit(player, QuestGizeltonCaravan, CaravanPointAfterSouthEscort);
                SouthQuest = false;
                break;
        }
    }

    private void Credit(Player? player, uint questId, uint resumePoint)
    {
        if (player is not null && System is { } system && system.QuestIncomplete(player, questId))
        {
            system.RewardGroupEventExplored(player, questId, Me);
        }

        // MoveWaypoint(19019, PATH_FROM_WAYPOINT_PATH) + SetNextWaypoint + SetWalk(false) + End(): taken on the next update, outside the
        // arrival callback of the escort's own move.
        _resumeAtPoint = resumePoint;
        _questId = 0;
        _caravan.Clear();
        QuestEnd();
        Stop();
    }

    /// <summary>UpdateEscortAI (:483-498) with CreatureGroupMemberDied (:551-555) read from the watched members.</summary>
    protected override void UpdateEscortAI(uint diffMs)
    {
        base.UpdateEscortAI(diffMs);
        if (_resumeAtPoint != 0)
        {
            uint point = _resumeAtPoint;
            _resumeAtPoint = 0;
            if (System is { } system)
            {
                system.SetScriptRun(Me, true);
                system.ResumeWaypointPath(Me, CaravanPathId, point);
            }
        }

        if (ScriptDone || _questId == 0)
        {
            return;
        }

        if (!StatusFailed && _caravan.Any(c => !c.IsAlive))
        {
            StatusFailed = true;
        }

        if (!StatusFailed)
        {
            return;
        }

        _failMs -= (int)Math.Min(diffMs, int.MaxValue);
        if (_failMs > 0 && CaravanInCombat())
        {
            return;
        }

        MakeQuestFailAndClean();
        ScriptDone = true;
    }

    private bool CaravanInCombat()
        => Me.Combat.IsInCombat || _caravan.Any(c => c.IsAlive && c.IsInWorld && c.Combat.IsInCombat);

    /// <summary>MakeQuestFailAndClean (:451-481): the quest fails for the group (or the player) and the caravan disappears.</summary>
    private void MakeQuestFailAndClean()
    {
        Player? player = GetPlayerForEscort();
        if (player is null)
        {
            return;
        }

        if (System is not { } system)
        {
            return;
        }

        // FailQuest for the members (or the player) who have either quest incomplete: GroupEventFailHappens does the group walk.
        foreach (uint quest in (ReadOnlySpan<uint>)[QuestBodyguardForHire, QuestGizeltonCaravan])
        {
            system.FailEscortQuest(player, quest);
        }

        Creature[] members = [.. _caravan.Where(c => c.IsInWorld)];
        _caravan.Clear();
        _questId = 0;
        Stop();
        foreach (Creature member in members)
        {
            system.ForcedDespawn(member, 0);
        }

        system.ForcedDespawn(Me, 0);
    }

    private bool Within(WorldObject other, float range)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y, dz = Me.Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= range * range;
    }
}

/// <summary>
/// Rigger Gizelton (entry 11626): mangos-classic ScriptDev2 <c>npc_rigger_gizelton</c> (kalimdor/desolace.cpp:569-591, registered :701-705).
/// Taking "Gizelton Caravan" (5943) from him tells the nearest Cork Gizelton within 100 yd which player to escort; Cork leads it.
/// </summary>
public sealed class RiggerGizeltonAI(Creature creature) : ScriptedAI(creature), IQuestScriptAI
{
    public const uint Entry = CorkGizeltonAI.NpcRiggerGizelton;
    public const float CorkRange = 100f;

    protected override void Reset()
    {
    }

    /// <summary>QuestAccept_npc_rigger_gizelton (:579-591): GetClosestCreatureWithEntry(NPC_CORK_GIZELTON, 100).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != CorkGizeltonAI.QuestGizeltonCaravan || System is not { } system)
        {
            return;
        }

        CorkGizeltonAI? cork = system.Creatures
            .Where(c => c.IsAlive && c.Entry == CorkGizeltonAI.Entry && DistanceSq(c) <= CorkRange * CorkRange)
            .OrderBy(DistanceSq)
            .Select(c => c.AI)
            .OfType<CorkGizeltonAI>()
            .FirstOrDefault();
        cork?.StartRiggerEscort(player);
    }

    private float DistanceSq(WorldObject other)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y, dz = Me.Z - other.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }
}
