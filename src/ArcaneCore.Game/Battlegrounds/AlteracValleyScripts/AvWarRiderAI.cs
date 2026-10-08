using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos AV_WarRiderAI (scripts/battlegrounds/battleground_alterac.cpp:3664-3790), the war riders and gryphons a wing commander leaves
/// (script av_warrider): flying, they take the enemy base as their home (Dun Baldar for the Horde's riders, Frostwolf Village for the
/// Alliance's gryphons), fly there, wander 55 yd around it, attack the nearest enemy within 50 yd and keep 25 yd from it, and within 30 yd
/// cast Fireball, Fireball Volley and Stun Bomb Attack.
/// </summary>
public sealed class AvWarRiderAI : CreatureAI
{
    public const uint SpellFireball = 22088;
    public const uint SpellFireballVolley = 15285;
    public const uint SpellStunBombAttack = 21188;

    /// <summary>The wander radius around the enemy base (SetWanderDistance(55)).</summary>
    public const float WanderDistance = 55f;

    private readonly AlteracValleyScripts _scripts;
    private uint _fireballTimer;
    private uint _fireballVolleyTimer;
    private uint _stunBombTimer;
    private bool _movingToPoint;

    public AvWarRiderAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
        CasterChaseDistance = 25f;
        Reset();
    }

    /// <summary>Whether the rider took the enemy base as its home (m_isMovingToPoint).</summary>
    public bool HeadingForTheEnemyBase => _movingToPoint;

    /// <summary>The home a war rider of <paramref name="entry"/> takes (UpdateAI, :3712-3733); null for another entry.</summary>
    public static (float X, float Y, float Z)? EnemyBase(uint entry) => entry switch
    {
        NpcWarRiderGuse or NpcWarRiderMulverick or NpcWarRiderJeztor => (618.4f, -87.97f, 85.77f),
        NpcGryphonSlidore or NpcGryphonIchman or NpcGryphonVipore => (-1311.53f, -355.28f, 130.93f),
        _ => null,
    };

    /// <summary>A war rider attacks on sight as any aggressive creature.</summary>
    public override bool AggroesOnSight => true;

    private void Reset()
    {
        AvScript.SetFly(Me, true);
        AvScript.SetWalk(Me, false);
        _fireballTimer = 0;
        _fireballVolleyTimer = 8000;
        _stunBombTimer = 13000;
    }

    public override void OnRespawn() => Reset();

    public override void OnEvade() => Reset();

    /// <summary>JustReachedHome (:3703-3706): wander around the base.</summary>
    public override void OnReachedHome()
        => Me.Motion.MoveRandom(new RandomMovementGenerator(WanderDistance, Me.Home, run: true));

    /// <summary>UpdateAI (:3708-3788).</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!_movingToPoint)
        {
            if (EnemyBase(Me.Template.Entry) is not { } home)
            {
                return;
            }

            System?.SetHomePosition(Me, home.X, home.Y, home.Z, 0f);
            _movingToPoint = true;
            System?.EnterEvadeMode(Me); // flies "home": the enemy base
            return;
        }

        if (!UpdateVictim() || Victim is null)
        {
            if (Me.Motion.CurrentType != MovementGeneratorType.Home && System?.SelectNearestTarget(Me, 50f) is { } target)
            {
                AttackStart(target);
            }

            return;
        }

        if (Victim is { } victim && Distance(victim) <= 30f)
        {
            if (_fireballTimer < diffMs)
            {
                if (DoCast(victim, SpellFireball) == CreatureCastResult.Ok)
                {
                    _fireballTimer = 5000;
                }
            }
            else
            {
                _fireballTimer -= diffMs;
            }

            if (_fireballVolleyTimer < diffMs)
            {
                if (DoCast(victim, SpellFireballVolley) == CreatureCastResult.Ok)
                {
                    _fireballVolleyTimer = 7500;
                }
            }
            else
            {
                _fireballVolleyTimer -= diffMs;
            }

            if (_stunBombTimer < diffMs)
            {
                if (DoCast(victim, SpellStunBombAttack) == CreatureCastResult.Ok)
                {
                    _stunBombTimer = 9000;
                }
            }
            else
            {
                _stunBombTimer -= diffMs;
            }
        }
    }

    private float Distance(Unit other)
    {
        float dx = Me.X - other.X;
        float dy = Me.Y - other.Y;
        float dz = Me.Z - other.Z;
        return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
