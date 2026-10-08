using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic blackrock_depths/boss_ambassador_flamelash.cpp: boss_ambassador_flamelashAI.</summary>
public sealed class AmbassadorFlamelashAI(Creature creature, BlackrockDepthsInstance instance) : CreatureAI(creature)
{
    private readonly uint[] _spiritMs = new uint[7];
    private readonly HashSet<ObjectGuid> _spirits = [];

    public override bool AggroesOnSight => true;

    public override void OnRespawn()
    {
        for (int i = 0; i < _spiritMs.Length; i++) _spiritMs[i] = (uint)(System?.RandomInt(0, 1000) ?? 0);
        _spirits.Clear();
    }

    public override void OnAggro(Unit target)
    {
        DoCast(Me, 15573);
        instance.SetData(BlackrockDepthsInstance.TypeFlamelash, EncounterState.InProgress);
        foreach (var at in new (float X, float Y, float Z, float O)[]
            { (919.21f, -231.029f, -50.1755f, 5.65487f), (913.883f, -236.914f, -49.8527f, 6.03884f),
              (924.225f, -256.302f, -49.8526f, 1.16937f), (932.524f, -252.475f, -49.8526f, 1.71042f) })
            System?.SummonAt(Me, BlackrockDepthsInstance.NpcFireguardDestroyer, at.X, at.Y, at.Z, at.O, null, 300_000);
    }

    public override void OnDeath(Unit? killer) => instance.SetData(BlackrockDepthsInstance.TypeFlamelash, EncounterState.Done);
    public override void OnReachedHome() => instance.SetData(BlackrockDepthsInstance.TypeFlamelash, EncounterState.Fail);

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Template.Entry == 9178)
        {
            summoned.Motion.MoveFollow(Me, 0, 0);
            _spirits.Add(summoned.Guid);
        }
    }

    public override void MoveInLineOfSight(Unit who)
    {
        base.MoveInLineOfSight(who);
        if (who is Creature { IsAlive: true } spirit && spirit.Template.Entry == 9178
            && _spirits.Contains(spirit.Guid) && WithinContact(spirit))
        {
            _spirits.Remove(spirit.Guid);
            System?.CastSpell(spirit, 13489, Me, triggered: true);
        }
    }

    /// <summary>
    /// pWho->IsWithinDistInMap(m_creature, 2 * CONTACT_DISTANCE): the 3D distance between the centres less both bounding radii is at most
    /// 2 * 0.5 yards.
    /// </summary>
    private bool WithinContact(Creature spirit)
    {
        float dx = spirit.X - Me.X, dy = spirit.Y - Me.Y, dz = spirit.Z - Me.Z;
        float reach = (2 * ContactDistance) + spirit.BoundingRadius + Me.BoundingRadius;
        return (dx * dx) + (dy * dy) + (dz * dz) <= reach * reach;
    }

    /// <summary>WorldObject::GetAngle from (x, y) to the boss, in [0, 2pi).</summary>
    private float AngleTo(float x, float y)
    {
        float angle = MathF.Atan2(Me.Y - y, Me.X - x);
        return angle >= 0 ? angle : angle + (2 * MathF.PI);
    }

    private const float ContactDistance = 0.5f;

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is null) return;
        for (int i = 0; i < _spiritMs.Length; i++)
        {
            if (_spiritMs[i] < diffMs)
            {
                // DoSummonSpirit: at the rune, facing the boss (pRune->GetAngle(m_creature)).
                if (instance.RuneAt(i) is { } rune)
                    System?.SummonAt(Me, 9178, rune.X, rune.Y, rune.Z, AngleTo(rune.X, rune.Y), null, 60_000);
                _spiritMs[i] = (uint)(System?.RandomInt(15_000, 30_000) ?? 15_000);
            }
            else _spiritMs[i] -= diffMs;
        }
    }
}
