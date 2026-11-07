using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos npc_AlteracBowmanAI (scripts/battlegrounds/battleground_alterac.cpp:1151-1230, script npc_AlteracBowman): the Stormpike and
/// Frostwolf tower bowmen (13358, 13359). A bowman roots itself (Root Self, 17507, permanent; put on again when it reaches home) and never
/// chases. A hostile unit within 80 yd and in line of sight draws its attack; it shoots (Shoot, 22121, every 1.7 s) while the victim is out of
/// melee reach, and evades when it has had no victim within 80 yd in sight for three seconds. World thread; one instance per creature.
/// <para>
/// Divergences: the evade timer only runs while the bowman is in combat (vmangos also evades an idle bowman every three seconds, which is
/// a no-op there but resets health and auras here); the on-sight test replaces vmangos' IsValidAttackTarget with the engine's own rules
/// minus the 3 yd height limit of ordinary aggro, which would blind a bowman on a tower to the ground below it.
/// </para>
/// </summary>
public sealed class AvBowmanAI : ScriptedAI
{
    public const uint EntryStormpikeBowman = 13358;
    public const uint EntryFrostwolfBowman = 13359;
    public const uint SpellShoot = 22121;
    public const uint SpellRootSelf = 17507;
    public const uint ShootSpeedMs = 1700;
    public const uint ResetDelayMs = 3000;
    public const float ShootRange = 80.0f;

    private uint _shootTimer;
    private uint _resetTimer;
    private bool _rootApplied;

    public AvBowmanAI(Creature creature)
        : base(creature)
    {
        CombatMovement = false; // SetCombatMovement(false)
        Reset();
    }

    /// <summary>Both bowman entries.</summary>
    public static IEnumerable<uint> Entries => [EntryStormpikeBowman, EntryFrostwolfBowman];

    protected override void Reset()
    {
        _shootTimer = 100;
        _resetTimer = ResetDelayMs;
    }

    /// <summary>JustReachedHome: the evade took the root off, put it on again.</summary>
    public override void OnReachedHome() => ApplyRoot();

    private void ApplyRoot()
    {
        DoAddAura(SpellRootSelf, permanent: true);
        _rootApplied = true;
    }

    /// <summary>TargetWithinShootRange: closer than 80 yd (bounding radii taken off, as WorldObject::GetDistance) and in line of sight.</summary>
    private bool TargetWithinShootRange(Unit target)
    {
        if (System is not { } system)
        {
            return false;
        }

        float distance = MathF.Sqrt(WorldGeometry.DistanceSq3D(Me, target)) - Me.BoundingRadius - target.BoundingRadius;
        return MathF.Max(distance, 0f) < ShootRange && system.IsInLineOfSight(Me, target);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (Victim is not null || System is not { } system || !who.IsAlive || !ReferenceEquals(who.Map, Me.Map)
            || who is Player { IsGameMaster: true } || who is Creature { IsInEvadeMode: true } || ReferenceEquals(who, Me)
            || Me.IsInEvadeMode || !system.CanInitiateAttack(Me) || !system.IsProximityAggroAllowedFor(Me, who)
            || !system.Map.Combat.CanAttackSingleTarget(Me, who) || !system.AiServices.Hostility.IsHostile(Me, who)
            || !TargetWithinShootRange(who))
        {
            return;
        }

        system.EnterCombatWithTarget(Me, who);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_rootApplied)
        {
            ApplyRoot(); // the constructor's AddAura: the creature is not in its map yet when the script is built
        }

        Unit? victim = Victim;
        if (Me.Combat.IsInCombat && !Me.IsInEvadeMode && (victim is null || !TargetWithinShootRange(victim)))
        {
            if (_resetTimer < diffMs)
            {
                _resetTimer = ResetDelayMs;
                EnterEvadeMode();
                return;
            }

            _resetTimer -= diffMs;
        }

        if (!UpdateVictim() || Victim is not { } target)
        {
            return;
        }

        if (_shootTimer < diffMs)
        {
            if (!MapCombat.CanReachWithMeleeAutoAttack(Me, target) && DoCast(target, SpellShoot) == CreatureCastResult.Ok)
            {
                _shootTimer = ShootSpeedMs;
            }
        }
        else
        {
            _shootTimer -= diffMs;
        }
    }
}
