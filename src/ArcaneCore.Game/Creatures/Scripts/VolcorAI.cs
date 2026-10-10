using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Volcor (entry 3692, Darkshore), the escort of quests 994 "Escape Through Force" and 995 "Escape Through Stealth": mangos-classic
/// ScriptDev2 <c>npc_volcor</c> as it was before the 2025 waypoint_path rework (kalimdor/darkshore.cpp at d6d00d8a46), the version that
/// matches classic-db z2815's single script_waypoint path (24 points): points 1-15 are the fight out, 16-24 the stealth way.
/// </summary>
public sealed class VolcorAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 3692, QuestEscapeThroughForce = 994, QuestEscapeThroughStealth = 995;
    public const uint NpcBlackwoodShaman = 2171, NpcBlackwoodUrsa = 2170, SpellMoonstalkerForm = 10849, FactionFriendly = 35;
    public const int SayStart = -1000789, SayEnd = -1000790, SayFirstAmbush = -1000791, SayEscape = -1000195;
    public static readonly int[] SayAggro = [-1000792, -1000793, -1000794];
    private const uint WaypointIdQuestStealth = 16;
    private const uint EmoteOneshotBow = 2;

    private static readonly (float X, float Y, float Z, float O)[] s_spawns =
    [
        (4630.2f, 22.6f, 70.1f, 2.4f), (4603.8f, 53.5f, 70.4f, 5.4f), (4627.5f, 100.4f, 62.7f, 5.8f),
        (4692.8f, 75.8f, 56.7f, 3.1f), (4747.8f, 152.8f, 54.6f, 2.4f), (4711.7f, 109.1f, 53.5f, 2.4f),
    ];

    private uint _questId;

    public List<Creature> Summoned { get; } = [];

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId is not (QuestEscapeThroughForce or QuestEscapeThroughStealth))
        {
            return;
        }

        Me.StandState = StandState.Stand;
        System?.SetFacingTo(Me, MathF.Atan2(player.Y - Me.Y, player.X - Me.X));
        _questId = questId;
        if (questId == QuestEscapeThroughStealth)
        {
            Me.FactionTemplate = FactionFriendly;
            Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
            Start(run: true, player: player, questId: questId);
            SetEscortPaused(true);
            SetCurrentWaypoint((int)WaypointIdQuestStealth - 1); // the z2815 path numbers its points 1..24 in order
            SetEscortPaused(false);
        }
        else
        {
            Start(run: false, player: player, questId: questId);
        }
    }

    protected override void Reset()
    {
        if (!HasEscortState(EscortState.Escorting))
        {
            _questId = 0;
        }
    }

    protected override void Aggro(Unit target)
    {
        if (System is not { } system)
        {
            return;
        }

        int roll = system.RandomInt(0, 4); // urand(0, 4): three of five rolls speak
        if (roll < SayAggro.Length)
        {
            system.SayText(Me, SayAggro[roll]);
        }
    }

    /// <summary>No combat for the stealth quest.</summary>
    public override void MoveInLineOfSight(Unit who)
    {
        if (_questId != QuestEscapeThroughStealth)
        {
            base.MoveInLineOfSight(who);
        }
    }

    private void Summon(uint entry, int spawn)
    {
        (float x, float y, float z, float o) = s_spawns[spawn];
        // TEMPSPAWN_TIMED_OOC_DESPAWN, 20000; JustSummoned: AttackStart(m_creature).
        if (System?.SummonAt(Me, entry, x, y, z, o, Me, 20_000) is { } summoned)
        {
            Summoned.Add(summoned);
        }
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 2:
                System?.SayText(Me, SayStart);
                break;
            case 5:
                Summon(NpcBlackwoodShaman, 0);
                Summon(NpcBlackwoodUrsa, 1);
                break;
            case 6:
                System?.SayText(Me, SayFirstAmbush);
                break;
            case 11:
                Summon(NpcBlackwoodShaman, 2);
                Summon(NpcBlackwoodUrsa, 3);
                // The source has no break here: point 11 also summons point 13's two Ursa.
                Summon(NpcBlackwoodUrsa, 4);
                Summon(NpcBlackwoodUrsa, 5);
                break;
            case 13:
                Summon(NpcBlackwoodUrsa, 4);
                Summon(NpcBlackwoodUrsa, 5);
                break;
            case 15:
                System?.SayText(Me, SayEnd);
                if (GetPlayerForEscort() is { } forced)
                {
                    System?.RewardGroupEventExplored(forced, QuestEscapeThroughForce, Me);
                }

                SetEscortPaused(true);
                System?.ForcedDespawn(Me, 10_000);
                break;
            case 16:
                System?.PlayEmote(Me, EmoteOneshotBow);
                break;
            case 17:
                if (GetPlayerForEscort() is { } escaping)
                {
                    System?.SayText(Me, SayEscape, escaping);
                }

                break;
            case 18:
                DoCast(Me, SpellMoonstalkerForm);
                break;
            case 24:
                if (GetPlayerForEscort() is { } stealthy)
                {
                    System?.RewardGroupEventExplored(stealthy, QuestEscapeThroughStealth, Me);
                }

                break;
        }
    }
}
