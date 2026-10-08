using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Ruul Snowhoof (entry 12818, Ashenvale), the escort of quest 6482 "Freedom to Ruul": mangos-classic ScriptDev2 <c>npc_ruul_snowhoof</c>
/// (AI/ScriptDevAI/scripts/kalimdor/ashenvale.cpp:373-472), whose <c>script_waypoint</c> path classic-db z2815 carries (36 points, path 0).
/// Taking the quest sets him off with the player under the passive escort faction; at points 14 and 31 three Thistlefur furbolgs ambush him,
/// at point 32 the quest's event objective is done for the player and the group near him, at 33 he bows, and at 36 he disappears (he is
/// back at the next respawn). His bear form (spell 20514) is cast and dropped as the script does when a spell system is present.
/// Not ported: SetImmuneToNPC around the turn-in (this server has no NPC-immunity flag handling for scripts).
/// </summary>
public sealed class RuulSnowhoofAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    /// <summary>creature_template.Entry of Ruul Snowhoof.</summary>
    public const uint Entry = 12818;

    /// <summary>QUEST_FREEDOM_TO_RUUL.</summary>
    public const uint QuestFreedomToRuul = 6482;

    /// <summary>The ambushers: NPC_T_AVENGER, NPC_T_SHAMAN, NPC_T_PATHFINDER.</summary>
    public static readonly uint[] Ambushers = [3925, 3924, 3926];

    /// <summary>SPELL_RUUL_SHAPECHANGE.</summary>
    public const uint SpellShapechange = 20514;

    /// <summary>FACTION_ESCORT_H_NEUTRAL_PASSIVE (ScriptDevAIMgr.h:45).</summary>
    public const uint FactionEscortHordeNeutralPassive = 33;

    /// <summary>SAY_RUUL_COMPLETE (a ScriptDev2 script text; logged once when the world has no such text).</summary>
    public const int SayComplete = -1010022;

    private const uint EmoteOneshotBow = 2;

    /// <summary>m_ruulAmbushCoords: the first and the second ambush.</summary>
    private static readonly (float X, float Y, float Z)[] s_ambushes = [(3425.33f, -595.93f, 178.31f), (3245.34f, -506.66f, 150.05f)];

    /// <summary>The ambushers summoned so far (for inspection).</summary>
    public List<Creature> Summoned { get; } = [];

    /// <summary>QuestAccept_npc_ruul_snowhoof: the passive escort faction (restored at respawn), standing up, and the escort with the player.</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestFreedomToRuul)
        {
            return;
        }

        Me.FactionTemplate = FactionEscortHordeNeutralPassive;
        Me.StandState = StandState.Stand;
        Start(run: false, player: player, questId: questId);
    }

    /// <summary>Reset: the bear form, triggered.</summary>
    protected override void Reset() => DoCast(Me, SpellShapechange, triggered: true);

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 14:
                SpawnAmbush(0);
                break;
            case 31:
                SpawnAmbush(1);
                break;
            case 32:
                System?.RemoveAuras(Me, SpellShapechange);
                if (GetPlayerForEscort() is { } rewarded)
                {
                    System?.SetFacingTo(Me, MathF.Atan2(rewarded.Y - Me.Y, rewarded.X - Me.X));
                    System?.RewardGroupEventExplored(rewarded, QuestFreedomToRuul, Me);
                }

                break;
            case 33:
                if (GetPlayerForEscort() is { } thanked)
                {
                    System?.SayText(Me, SayComplete, thanked);
                    System?.SetFacingTo(Me, MathF.Atan2(thanked.Y - Me.Y, thanked.X - Me.X));
                }

                System?.PlayEmote(Me, EmoteOneshotBow);
                SetRun(true);
                break;
            case 34:
                DoCast(Me, SpellShapechange);
                break;
            case 36:
                SetRun(false);
                System?.ForcedDespawn(Me, 0);
                break;
        }
    }

    /// <summary>DoSpawnAmbush: the three furbolgs around the ambush point (7 yd), for 60 s after death, running at Ruul.</summary>
    private void SpawnAmbush(int index)
    {
        if (System is not { } system)
        {
            return;
        }

        (float x, float y, float z) = s_ambushes[index];
        foreach (uint entry in Ambushers)
        {
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            float distance = Random.Shared.NextSingle() * 7f;
            if (system.SummonAt(Me, entry, x + (distance * MathF.Cos(angle)), y + (distance * MathF.Sin(angle)), z, 0f, null, 60_000) is { } ambusher)
            {
                Summoned.Add(ambusher);
                system.MoveTo(ambusher, Me.X, Me.Y, Me.Z, run: true, finalOrientation: null);
            }
        }
    }
}
