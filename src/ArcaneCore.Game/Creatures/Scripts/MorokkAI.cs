using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Overlord Mok'Morokk (entry 4500, Dustwallow Marsh), the escort of "Challenge Overlord Mok'Morokk" (quest 1173): mangos-classic ScriptDev2
/// <c>npc_morokk</c> (kalimdor/dustwallow_marsh.cpp:41-163, registered :1227-1231) on his eight point script_waypoint path. Accepting the quest
/// starts him at a run. At point 1 he stops (WaypointReached :87-95); while paused and out of combat he challenges the player if they are
/// alive and within 70 yd, with his line (-1000499), no longer immune to players, aggressive, faction 168, and attacks them, and otherwise
/// walks on (UpdateEscortAI :97-141). Brought down to 30% he has lost: the group gets the event credit, he turns friendly (35), his
/// fight ends, he is immune to players and passive again, says he is scared (-1000500) and walks on with his motion cleared. Respawning gives
/// him the spawn faction (29) back (JustRespawned :69-73). Reaching home while escorting without having lost fails the quest for the group
/// and despawns him (JustReachedHome :75-85).
/// </summary>
public sealed class MorokkAI(Creature creature) : EscortAI(creature), IQuestScriptAI
{
    public const uint Entry = 4500, QuestChallengeMorokk = 1173, FactionHostile = 168, FactionRunning = 35, FactionSpawn = 29;
    public const int SayChallenge = -1000499, SayScared = -1000500;
    public const float ChallengeRange = 70f, BeatenHealthPercent = 30f;

    private bool _isSuccess;

    /// <summary>m_bIsSuccess: the player has beaten him this time (set false at each challenge).</summary>
    public bool IsSuccess => _isSuccess;

    /// <summary>QuestAccept_npc_morokk (:151-163).</summary>
    public void OnQuestAccept(Player player, uint questId)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (questId == QuestChallengeMorokk)
        {
            Start(run: true, player: player, questId: questId);
        }
    }

    protected override void JustRespawned()
    {
        base.JustRespawned();
        Me.FactionTemplate = FactionSpawn;
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        if (!HasEscortState(EscortState.Escorting) || _isSuccess)
        {
            return;
        }

        if (GetPlayerForEscort() is { } player)
        {
            System?.FailEscortQuest(player, QuestChallengeMorokk); // FailQuestForPlayerAndGroup
        }

        System?.ForcedDespawn(Me, 0);
    }

    protected override void WaypointReached(uint pointId)
    {
        if (pointId == 1)
        {
            SetEscortPaused(true);
        }
    }

    protected override void UpdateEscortAI(uint diffMs)
    {
        if (!UpdateVictim() || Victim is null)
        {
            if (HasEscortState(EscortState.Paused))
            {
                if (GetPlayerForEscort() is { IsAlive: true } player && IsInRange(player, ChallengeRange))
                {
                    _isSuccess = false;
                    System?.SayText(Me, SayChallenge, player);
                    Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
                    Me.ReactState = CreatureReactState.Aggressive;
                    Me.FactionTemplate = FactionHostile;
                    AttackStart(player);
                }
                else
                {
                    SetEscortPaused(false);
                }
            }

            return;
        }

        if (Me.HealthPercent <= BeatenHealthPercent && HasEscortState(EscortState.Paused) && GetPlayerForEscort() is { } winner)
        {
            System?.RewardGroupEventExplored(winner, QuestChallengeMorokk, Me);
            Me.FactionTemplate = FactionRunning;
            _isSuccess = true;
            System?.ResetAurasOnEvade(Me);
            System?.StopCombatInPlace(Me); // CombatStop(true)
            Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
            SetEscortPaused(false);
            Me.ReactState = CreatureReactState.Passive;
            System?.SayText(Me, SayScared);
            Me.Motion.Clear(); // source: Clear(false); ours has no such flag and acts like Clear(true) (see the batch 5 doc)
        }
    }

    /// <summary>WorldObject::IsInRange(obj, 0, range) (Object.cpp:1401-1420): the 3D distance is under the range plus both combat reaches.</summary>
    private bool IsInRange(Unit other, float range)
    {
        float dx = Me.X - other.X, dy = Me.Y - other.Y, dz = Me.Z - other.Z;
        float reach = range + Me.GetFloat(UpdateFields.UnitFieldCombatreach) + other.GetFloat(UpdateFields.UnitFieldCombatreach);
        return (dx * dx) + (dy * dy) + (dz * dz) < reach * reach;
    }
}
