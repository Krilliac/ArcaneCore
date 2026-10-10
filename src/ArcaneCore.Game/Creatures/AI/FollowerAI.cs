using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures;

/// <summary>
/// mangos-classic ScriptDev2 <c>FollowerAI</c> (AI/ScriptDevAI/base/follower_ai.cpp and .h at 3e8597afe7): a quest NPC that follows the
/// player instead of walking a path. <see cref="StartFollow"/> sets the temporary faction, clears the npc flags and follows the leader at
/// the pet distance and angle. Every second out of combat it checks the leader or a group member is within 100 yards (else it despawns),
/// and despawns once <see cref="SetFollowComplete"/> was called without a post event. After a fight it walks back to where the fight began
/// and follows again; a pause (<see cref="SetFollowPaused"/>) holds it where it is. Its death fails the quest for the leader's group.
/// Not ported: AssistPlayerInCombat, which needs CREATURE_TYPEFLAGS_CAN_ASSIST, a template field ArcaneCore does not load.
/// </summary>
public abstract class FollowerAI(Creature creature) : CreatureAI(creature)
{
    /// <summary>MAX_PLAYER_DISTANCE.</summary>
    public const float MaxPlayerDistance = 100f;

    /// <summary>POINT_COMBAT_START: the walk back to where the fight began.</summary>
    public const uint PointCombatStart = 0xFFFFFF;

    /// <summary>PET_FOLLOW_DIST and PET_FOLLOW_ANGLE (mangos-classic Pet.h).</summary>
    public const float FollowDistance = 1f, FollowAngle = MathF.PI / 2;

    [Flags]
    public enum FollowState : uint
    {
        None = 0x000,
        InProgress = 0x001,
        Returning = 0x002,
        Paused = 0x004,
        Complete = 0x008,
        PreEvent = 0x010,
        PostEvent = 0x020,
    }

    private ObjectGuid _leaderGuid;
    private uint _updateFollowMs = 2500, _questForFollow;
    private bool _respawnedOnce;

    public FollowState State { get; private set; }

    public bool HasFollowState(FollowState state) => (State & state) != 0;

    private void AddFollowState(FollowState state) => State |= state;

    private void RemoveFollowState(FollowState state) => State &= ~state;

    /// <summary>ScriptedAI::Reset: the first spawn, every evade and every respawn.</summary>
    protected virtual void Reset()
    {
    }

    protected virtual void Aggro(Unit target)
    {
    }

    /// <summary>The script's own update (UpdateFollowerAI); by default it keeps the victim from the threat list.</summary>
    protected virtual void UpdateFollowerAI(uint diffMs) => UpdateVictim();

    /// <summary>The first call is the spawn; every later one a respawn after the corpse went (CorpseRemoved: no follow, combat movement on).</summary>
    public sealed override void OnRespawn()
    {
        if (_respawnedOnce)
        {
            State = FollowState.None;
            _leaderGuid = default;
            _questForFollow = 0;
            CombatMovement = true;
            if (Me.FactionTemplate != Me.Template.Faction)
            {
                Me.FactionTemplate = Me.Template.Faction; // TEMPFACTION_RESTORE_RESPAWN
            }
        }

        _respawnedOnce = true;
        Reset();
    }

    public sealed override void OnAggro(Unit target) => Aggro(target);

    /// <summary>JustDied: a follow with a quest fails it for the leader and his group.</summary>
    public override void OnDeath(Unit? killer)
    {
        if (!HasFollowState(FollowState.InProgress) || _leaderGuid.IsEmpty || _questForFollow == 0)
        {
            return;
        }

        if (GetLeaderForFollower() is { } player)
        {
            System?.FailEscortQuest(player, _questForFollow);
        }
    }

    /// <summary>EnterEvadeMode: a following creature walks back to where the fight began instead of going home.</summary>
    public override void OnEvade()
    {
        if (HasFollowState(FollowState.InProgress))
        {
            Me.IsEvading = false; // no run home
            if (Me.Motion.CurrentType == MovementGeneratorType.Chase || Me.Motion.CurrentType == MovementGeneratorType.Home)
            {
                Me.Motion.Clear();
                CreatureHome start = Me.CombatStart ?? new CreatureHome(Me.X, Me.Y, Me.Z, Me.Orientation);
                Me.Motion.MovePoint(PointCombatStart, start.X, start.Y, start.Z, run: true);
            }
        }

        Reset();
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || !HasFollowState(FollowState.InProgress) || pointId != PointCombatStart)
        {
            return;
        }

        if (GetLeaderForFollower() is not null)
        {
            if (!HasFollowState(FollowState.Paused))
            {
                AddFollowState(FollowState.Returning);
            }
        }
        else
        {
            System?.ForcedDespawn(Me, 0);
        }
    }

    public sealed override void OnUpdate(uint diffMs)
    {
        if (HasFollowState(FollowState.InProgress) && Me.Combat.Victim is null)
        {
            if (_updateFollowMs < diffMs)
            {
                if (HasFollowState(FollowState.Complete) && !HasFollowState(FollowState.PostEvent))
                {
                    System?.ForcedDespawn(Me, 0);
                    return;
                }

                bool maxRangeExceeded = true;
                if (GetLeaderForFollower() is { } player)
                {
                    if (HasFollowState(FollowState.Returning))
                    {
                        RemoveFollowState(FollowState.Returning);
                        Me.Motion.MoveFollow(player, FollowDistance, FollowAngle);
                        return;
                    }

                    IReadOnlyList<Player> members = System?.EscortGroupMembers(player) ?? [];
                    maxRangeExceeded = !(members.Count > 0 ? members : [player]).Any(InRange);
                }

                if (maxRangeExceeded)
                {
                    System?.ForcedDespawn(Me, 0);
                    return;
                }

                _updateFollowMs = 1000;
            }
            else
            {
                _updateFollowMs -= diffMs;
            }
        }

        UpdateFollowerAI(diffMs);
    }

    /// <summary>IsWithinDistInMap(member, MAX_PLAYER_DISTANCE): 3D, bounding radii added.</summary>
    protected bool InRange(Unit member)
    {
        if (!ReferenceEquals(member.Map, Me.Map))
        {
            return false;
        }

        float dx = Me.X - member.X, dy = Me.Y - member.Y, dz = Me.Z - member.Z;
        float reach = MaxPlayerDistance + Me.BoundingRadius + member.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }

    /// <summary>StartFollow: not in combat and not already following.</summary>
    public void StartFollow(Player leader, uint factionForFollower = 0, uint questId = 0)
    {
        ArgumentNullException.ThrowIfNull(leader);
        if (Me.Combat.Victim is not null || HasFollowState(FollowState.InProgress))
        {
            return;
        }

        _leaderGuid = leader.Guid;
        if (factionForFollower != 0)
        {
            Me.FactionTemplate = factionForFollower;
        }

        _questForFollow = questId;
        if (Me.Motion.CurrentType == MovementGeneratorType.Waypoint)
        {
            Me.Motion.Clear();
        }

        Me.NpcFlags = 0;
        AddFollowState(FollowState.InProgress);
        Me.Motion.MoveFollow(leader, FollowDistance, FollowAngle);
    }

    /// <summary>GetLeaderForFollower: the leader while alive, else a living group member within 100 yards, who becomes the leader.</summary>
    protected Player? GetLeaderForFollower()
    {
        if (_leaderGuid.IsEmpty || System?.Map.FindPlayer(_leaderGuid) is not { } leader)
        {
            return null;
        }

        if (leader.IsAlive)
        {
            return leader;
        }

        foreach (Player member in System.EscortGroupMembers(leader))
        {
            if (member.IsAlive && InRange(member))
            {
                _leaderGuid = member.Guid;
                return member;
            }
        }

        return null;
    }

    /// <summary>SetFollowComplete: stop following; without a post event it despawns on the next follow check.</summary>
    public void SetFollowComplete(bool withEndEvent = false)
    {
        if (Me.Motion.CurrentType == MovementGeneratorType.Follow)
        {
            Me.Motion.Clear();
        }

        if (withEndEvent)
        {
            AddFollowState(FollowState.PostEvent);
        }
        else
        {
            RemoveFollowState(FollowState.PostEvent);
        }

        AddFollowState(FollowState.Complete);
    }

    /// <summary>SetFollowPaused: hold where it stands, or follow the leader again.</summary>
    public void SetFollowPaused(bool paused)
    {
        if (!HasFollowState(FollowState.InProgress) || HasFollowState(FollowState.Complete))
        {
            return;
        }

        if (paused)
        {
            AddFollowState(FollowState.Paused);
            if (Me.Motion.CurrentType == MovementGeneratorType.Follow)
            {
                Me.Motion.Clear();
            }
        }
        else
        {
            RemoveFollowState(FollowState.Paused);
            if (GetLeaderForFollower() is { } leader)
            {
                Me.Motion.MoveFollow(leader, FollowDistance, FollowAngle);
            }
        }
    }
}
