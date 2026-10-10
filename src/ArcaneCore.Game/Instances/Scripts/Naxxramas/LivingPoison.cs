using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// mangos-classic eastern_kingdoms/naxxramas/naxxramas.cpp npc_living_poisonAI (16027): no melee, never attacks, and casts Explode (28433,
/// triggered) on itself when a unit comes within 4 yd (2D, bounding radii).
/// </summary>
public sealed class LivingPoisonAI : CreatureAI
{
    public const uint Entry = 16027, ExplodeSpell = 28433;
    public const float ExplodeRange = 4f;

    public LivingPoisonAI(Creature creature) : base(creature) => MeleeEnabled = false;

    public override bool AttackStart(Unit target) => false;

    public override void MoveInLineOfSight(Unit who)
    {
        if (!Me.IsAlive || !who.IsAlive) return;
        float dx = Me.X - who.X, dy = Me.Y - who.Y;
        float distance = MathF.Sqrt((dx * dx) + (dy * dy)) - Me.BoundingRadius - who.BoundingRadius;
        if (distance > ExplodeRange) return;
        DoCast(Me, ExplodeSpell, triggered: true);
    }

    public override void OnUpdate(uint diffMs) { }
}

/// <summary>mangos-classic instance_naxxramas::OnCreatureCreate (NPC_NAXXRAMAS_TRIGGER) and ::Update: the Patchwerk corridor poison stream.</summary>
public sealed partial class NaxxramasInstance
{
    public const uint NaxxramasTrigger = 16082;
    public const uint LivingPoisonIntervalMs = 5000, LivingPoisonLifetimeMs = 15000;

    // naxxramas.h livingPoisonPositions: three starts, then their three ends.
    private static readonly (float X, float Y, float Z, float O)[] LivingPoisonPositions =
    [
        (3128.692f, -3119.211f, 293.346f, 4.725505f),
        (3154.432f, -3125.669f, 293.408f, 4.456693f),
        (3175.614f, -3134.716f, 293.282f, 4.244928f),
        (3128.709f, -3157.404f, 293.3238f, 4.725505f),
        (3145.881f, -3158.563f, 293.3216f, 4.456693f),
        (3157.736f, -3164.859f, 293.2874f, 4.244928f),
    ];

    private uint _livingPoisonMs;

    /// <summary>Time to the next three Living Poisons, 0 when the stream has not started (no trigger yet).</summary>
    public uint LivingPoisonTimerMs => _livingPoisonMs;

    private void OnLivingPoisonTriggerCreated(Creature creature)
    {
        if (creature.Entry == NaxxramasTrigger) _livingPoisonMs = LivingPoisonIntervalMs;
    }

    private void UpdateLivingPoison(uint diffMs)
    {
        if (_livingPoisonMs == 0) return;
        if (_livingPoisonMs > diffMs)
        {
            _livingPoisonMs -= diffMs;
            return;
        }

        _livingPoisonMs = LivingPoisonIntervalMs;
        if (GetSingleCreatureFromStorage(NaxxramasTrigger) is not { } trigger
            || Instance.FindUpdater<CreatureMapSystem>() is not { } system
            || system.Content.FindTemplate(LivingPoisonAI.Entry) is not { } template) return;
        for (int i = 0; i < 3; i++)
        {
            var (x, y, z, o) = LivingPoisonPositions[i];
            Creature poison = system.SpawnTemporary(template, x, y, z, o, trigger);
            var end = LivingPoisonPositions[i + 3];
            poison.Motion.MovePoint(0, end.X, end.Y, end.Z, run: false);
            system.ForcedDespawn(poison, LivingPoisonLifetimeMs);
        }
    }
}
