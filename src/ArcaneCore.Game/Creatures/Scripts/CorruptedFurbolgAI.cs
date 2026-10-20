using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Creatures.Scripts;

/// <summary>
/// Blackwood Warrior (2168) and Blackwood Totemic (2169), Darkshore: mangos-classic ScriptDev2 <c>npc_corrupted_furbolg</c>
/// (kalimdor/darkshore.cpp:656-820). In a fight the warrior, which takes Battle Stance (7165) on reset, uses Thunderclap (8078) after 16-21 s
/// and again every 16-21 s while someone is in melee range. The totemic drops a Healing Ward (5605) after 6-12 s and again every 30-36 s.
/// Either one flees once at 15% health. When quest 4763's purified food is put on the bonfire (event_purify_food,
/// <see cref="Instances.Scripts.World.KalimdorWorldMapData"/>), <see cref="SeekPurification"/> lures the furbolg: it stops fighting,
/// becomes immune to players and runs to the bowl. 5 s after it arrives it turns friendly (faction 35 until combat stops), wanders within
/// 40 yd of the bowl, and 90 s later goes home. The first furbolg drawn emotes both steps.
/// </summary>
public sealed class CorruptedFurbolgAI(Creature creature) : ScriptedAI(creature)
{
    public const uint NpcBlackwoodWarrior = 2168, NpcBlackwoodTotemic = 2169;
    public const uint SpellBattleStance = 7165, SpellThunderclap = 8078, SpellHealingWard = 5605, FactionBlackwood = 35;
    public const int EmoteLured = -1010027, EmotePurified = -1010028, EmoteGenericFlee = -1000007;
    public const uint PurifyDelayMs = 5000, PurifiedMs = 90_000, FleeMs = 7000;
    public const float FleeHealthPercent = 15f, WanderRadius = 40f, MaxBowlDistance = 60f, MeleeReach = 5f;

    private uint _actionMs, _startMs, _purifyMs, _resetMs;
    private bool _startPending, _hasFled, _isFirst, _passive;
    private (float X, float Y, float Z) _bowl;

    /// <summary>Lured or purified: the script holds its combat actions (SetCombatScriptStatus) and does not react (REACT_PASSIVE).</summary>
    public bool IsPassive => _passive;

    /// <summary>npc_corrupted_furbolgAI EVENT_START_PURIFICATION SetCombatScriptStatus(true) (darkshore.cpp:717), dropped by Reset (:752).</summary>
    public override bool CombatScriptRunning => _passive;

    public bool IsPurified => _resetMs != 0;

    private bool IsWarrior => Me.Template.Entry == NpcBlackwoodWarrior;

    protected override void Reset()
    {
        if (IsWarrior)
        {
            DoCast(Me, SpellBattleStance);
        }

        _actionMs = IsWarrior ? RandomMs(16_000, 21_000) : RandomMs(6000, 12_000);
        _hasFled = false;
        _isFirst = false;
        _passive = false;
        _startPending = false;
        _startMs = _purifyMs = _resetMs = 0;
        Me.UnitFlags &= ~UnitFlags.ImmuneToPlayer;
    }

    private uint RandomMs(int min, int max) => (uint)(System?.RandomInt(min, max) ?? min);

    public override void MoveInLineOfSight(Unit who)
    {
        if (!_passive)
        {
            base.MoveInLineOfSight(who);
        }
    }

    public override bool AttackStart(Unit target) => !_passive && base.AttackStart(target);

    /// <summary>SeekPurification: the bowl, and whether this is the furbolg that emotes; the first starts at once, the others within 5 s.</summary>
    public void SeekPurification(bool isFirst, float bowlX, float bowlY, float bowlZ)
    {
        _isFirst = isFirst;
        _bowl = (bowlX, bowlY, bowlZ);
        _startPending = true;
        _startMs = isFirst ? 0 : RandomMs(0, 5000);
    }

    /// <summary>EVENT_START_PURIFICATION: within 60 yd of the bowl the furbolg drops the fight and runs to it.</summary>
    private void StartPurification()
    {
        float dx = _bowl.X - Me.X, dy = _bowl.Y - Me.Y, dz = _bowl.Z - Me.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > MaxBowlDistance * MaxBowlDistance)
        {
            return;
        }

        if (_isFirst)
        {
            System?.SayText(Me, EmoteLured);
        }

        _passive = true;
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer;
        Me.Map?.Combat.CombatStop(Me);

        // GetNearPointAt(bowl, bounding radius, CONTACT_DISTANCE * 2, angle + PI): the side of the bowl facing the furbolg.
        float length = MathF.Sqrt((dx * dx) + (dy * dy));
        float reach = Me.BoundingRadius + 1f;
        (float x, float y) = length > 0.01f ? (_bowl.X - (dx / length * reach), _bowl.Y - (dy / length * reach)) : (_bowl.X, _bowl.Y);
        Me.Motion.Clear();
        Me.Motion.MovePoint(1, x, y, _bowl.Z, run: true);
    }

    public override void OnMovementInform(MovementGeneratorType type, uint pointId)
    {
        if (type != MovementGeneratorType.Point || pointId == 0 || !_passive)
        {
            return;
        }

        System?.MoveIdle(Me);
        _purifyMs = PurifyDelayMs;
    }

    /// <summary>EVENT_BECOME_PURIFIED.</summary>
    private void BecomePurified()
    {
        if (_isFirst)
        {
            System?.SayText(Me, EmotePurified);
        }

        Me.SetFactionTemporary(FactionBlackwood, TemporaryFactionFlags.RestoreCombatStop);
        Me.Motion.Clear();
        Me.Motion.MoveRandom(new RandomMovementGenerator(WanderRadius, new CreatureHome(_bowl.X, _bowl.Y, _bowl.Z, Me.Orientation), run: false));
        _resetMs = PurifiedMs;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_startPending)
        {
            if (_startMs <= diffMs)
            {
                _startPending = false;
                StartPurification();
            }
            else
            {
                _startMs -= diffMs;
            }
        }

        if (_purifyMs != 0)
        {
            if (_purifyMs <= diffMs)
            {
                _purifyMs = 0;
                BecomePurified();
            }
            else
            {
                _purifyMs -= diffMs;
            }
        }

        if (_resetMs != 0)
        {
            if (_resetMs <= diffMs)
            {
                // EVENT_FURBOLG_RESET: evade (which gives the faction back and resets the script) and go home.
                _resetMs = 0;
                EnterEvadeMode();
                return;
            }

            _resetMs -= diffMs;
        }

        if (_passive || !UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (!_hasFled && Me.MaxHealth > 0 && Me.Health * 100f / Me.MaxHealth <= FleeHealthPercent)
        {
            System?.SayText(Me, EmoteGenericFlee);
            Me.Motion.MoveFleeing(victim, FleeMs);
            _hasFled = true;
        }

        if (_actionMs > diffMs)
        {
            _actionMs -= diffMs;
            return;
        }

        if (IsWarrior)
        {
            float mx = victim.X - Me.X, my = victim.Y - Me.Y, reach = MeleeReach + Me.BoundingRadius + victim.BoundingRadius;
            if ((mx * mx) + (my * my) <= reach * reach && DoCast(Me, SpellThunderclap) == CreatureCastResult.Ok)
            {
                _actionMs = RandomMs(16_000, 21_000);
            }
        }
        else if (DoCast(Me, SpellHealingWard) == CreatureCastResult.Ok)
        {
            _actionMs = RandomMs(30_000, 36_000);
        }
    }
}
