using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Classic;

/// <summary>mangos-classic blackrock_depths/boss_emperor_dagran_thaurissan.cpp: boss_emperor_dagran_thaurissanAI.</summary>
public sealed class EmperorDagranAI(Creature creature, BlackrockDepthsInstance instance) : CreatureAI(creature)
{
    private uint _handMs = 4000, _avatarMs = 25_000;
    public override bool AggroesOnSight => true;

    public override void OnRespawn() { _handMs = 4000; _avatarMs = 25_000; }

    public override void OnAggro(Unit target)
    {
        System?.SayText(Me, new[] { -1230001, -1230064, -1230065 }[System?.RandomInt(0, 2) ?? 0]);
        DoCallForHelp(100);
    }

    public override void OnDeath(Unit? killer)
    {
        if (instance.Princess is { IsAlive: true } princess && princess.Template.Entry == BlackrockDepthsInstance.NpcPrincess)
        {
            princess.FactionTemplate = 734;
            princess.AI?.EnterEvadeMode();
        }
    }

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, -1230002);

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim) return;
        if (_handMs < diffMs)
        {
            Unit target = Me.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive).OrderBy(_ => System?.RandomInt(0, int.MaxValue)).FirstOrDefault() ?? victim;
            if (DoCast(target, 17492) == CreatureCastResult.Ok) _handMs = (uint)(System?.RandomInt(5000, 10_000) ?? 5000);
        }
        else _handMs -= diffMs;
        if (_avatarMs < diffMs)
        {
            if (DoCast(victim, 15636) == CreatureCastResult.Ok) _avatarMs = 18_000;
        }
        else _avatarMs -= diffMs;
    }
}

/// <summary>mangos-classic blackrock_depths/boss_emperor_dagran_thaurissan.cpp: boss_moira_bronzebeardAI.</summary>
public sealed class MoiraBronzebeardAI(Creature creature, BlackrockDepthsInstance instance) : CreatureAI(creature)
{
    private uint _healMs = 12_000, _blastMs = 16_000, _painMs = 2000, _smiteMs = 8000;
    public override bool AggroesOnSight => true;

    public override void OnRespawn()
    {
        _healMs = 12_000; _blastMs = 16_000; _painMs = 2000; _smiteMs = 8000;
        CasterChaseDistance = 25;
        MeleeEnabled = false;
    }

    public override void OnReachedHome()
    {
        if (instance.Emperor is { IsAlive: false }) DoCast(Me, 13912);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim) return;
        Tick(ref _blastMs, diffMs, 14_000, () => DoCast(victim, 15587));
        Tick(ref _painMs, diffMs, 18_000, () => DoCast(victim, 15654));
        Tick(ref _smiteMs, diffMs, 10_000, () => DoCast(victim, 10934));
        if (instance.Emperor is { IsAlive: true } emperor && emperor.Health < emperor.MaxHealth)
            Tick(ref _healMs, diffMs, 10_000, () => DoCast(emperor, 15586));
        else if (_healMs >= diffMs) _healMs -= diffMs;
    }

    private static void Tick(ref uint remaining, uint diffMs, uint resetMs, Func<CreatureCastResult> cast)
    {
        if (remaining < diffMs)
        {
            if (cast() == CreatureCastResult.Ok) remaining = resetMs;
        }
        else remaining -= diffMs;
    }
}
