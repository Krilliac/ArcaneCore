using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// A-Me 01 (entry 9623, Un'Goro Crater), the escort of quest 4245 "Chasing A-Me 01": mangos-classic ScriptDev2 <c>npc_ame01</c>
/// (AI/ScriptDevAI/scripts/kalimdor/ungoro_crater.cpp, npc_ame01AI and QuestAccept_npc_ame01) on the classic-db z2815 script_waypoint path
/// (40 points). She lies dead until the quest is taken, then walks under the passive escort faction of the player's team.
/// </summary>
public sealed class AMe01AI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 9623;
    public const uint QuestChasingAMe = 4245;
    public const int SayStart = -1000446, SayProgress = -1000447, SayEnd = -1000448;
    public static readonly int[] SayAggro = [-1000449, -1000450, -1000451];

    /// <summary>FACTION_ESCORT_A_PASSIVE and FACTION_ESCORT_H_PASSIVE (ScriptDevAIMgr.h).</summary>
    public const uint FactionEscortAlliancePassive = 774, FactionEscortHordePassive = 775;

    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId != QuestChasingAMe)
        {
            return;
        }

        Me.StandState = StandState.Stand;
        // SetFactionTemporary(..., TEMPFACTION_RESTORE_RESPAWN | TEMPFACTION_TOGGLE_IMMUNE_TO_NPC)
        Me.FactionTemplate = player.Team == Team.Alliance ? FactionEscortAlliancePassive : FactionEscortHordePassive;
        Me.UnitFlags &= ~UnitFlags.ImmuneToNpc;
        Start(run: false, player: player, questId: questId);
    }

    /// <summary>The constructor's SetStandState(UNIT_STAND_STATE_DEAD, true).</summary>
    protected override void JustSpawned() => Me.StandState = StandState.Dead;

    protected override void JustRespawned()
    {
        base.JustRespawned();
        Me.StandState = StandState.Dead;
    }

    protected override void WaypointReached(uint pointId)
    {
        switch (pointId)
        {
            case 1:
                System?.SayText(Me, SayStart);
                break;
            case 20:
                System?.SayText(Me, SayProgress);
                break;
            case 38:
                System?.SayText(Me, SayEnd);
                if (GetPlayerForEscort() is { } player)
                {
                    System?.RewardGroupEventExplored(player, QuestChasingAMe, Me);
                }

                break;
        }
    }

    /// <summary>Aggro: only a non-player attacker that the escort player is not already fighting, one of three lines naming it.</summary>
    protected override void Aggro(Unit target)
    {
        if (target is Player || GetPlayerForEscort() is not { } player || ReferenceEquals(player.Combat.Victim, target) || System is not { } system)
        {
            return;
        }

        system.SayText(Me, SayAggro[system.RandomInt(0, SayAggro.Length - 1)], target);
    }
}
