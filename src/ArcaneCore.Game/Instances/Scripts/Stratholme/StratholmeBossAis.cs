using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>boss_baroness_anastariAI (mangos-classic stratholme/boss_baroness_anastari.cpp: Reset, UpdateAI).</summary>
public sealed class BaronessAnastariAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _wail, _curse, _silence, _possess;

    public override void OnRespawn()
    {
        _wail = 0;
        _curse = 10_000;
        _silence = 25_000;
        _possess = 15_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _wail, diffMs, RandomThreatTarget(), 16565, 2_000, 3_000);
        CastWhenReady(ref _curse, diffMs, Me, 16867, 20_000, 20_000);
        CastWhenReady(ref _silence, diffMs, RandomThreatTarget(), 18327, 25_000, 25_000);
        CastWhenReady(ref _possess, diffMs, RandomThreatTarget(skipVictim: true, playerOnly: true), 17244, 30_000, 30_000);
    }
}

/// <summary>boss_maleki_the_pallidAI (mangos-classic stratholme/boss_maleki_the_pallid.cpp: Reset, UpdateAI).</summary>
public sealed class MalekiThePallidAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _mana, _frostbolt, _tomb, _life;

    public override void OnRespawn()
    {
        _mana = 30_000;
        _frostbolt = 0;
        _tomb = 15_000;
        _life = 20_000;
        DoCast(Me, 12556, triggered: true); // Frost Armor
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _frostbolt, diffMs, RandomThreatTarget(), 17503, 3_000, 4_000);
        Unit? tombTarget = RandomThreatTarget(skipVictim: true);
        if (CastWhenReady(ref _tomb, diffMs, tombTarget, 16869, 15_000, 20_000) && tombTarget is not null)
        {
            Me.Combat.Threat.Remove(tombTarget);
        }

        CastWhenReady(ref _life, diffMs, RandomThreatTarget(), 17238, 15_000, 20_000);
        CastWhenReady(ref _mana, diffMs, RandomThreatTarget(), 17243, 20_000, 30_000);
    }
}

/// <summary>boss_cannon_master_willeyAI (mangos-classic stratholme/boss_cannon_master_willey.cpp: Reset, JustSummoned, UpdateAI).</summary>
public sealed class CannonMasterWilleyAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _shoot, _pummel, _knock, _riflemen;

    public override void OnRespawn()
    {
        _shoot = 1_000;
        _pummel = 7_000;
        _knock = 11_000;
        _riflemen = 15_000;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (Victim is { } victim)
        {
            summoned.AI?.AttackStart(victim);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _pummel, diffMs, Victim, 15615, 12_000, 12_000);
        CastWhenReady(ref _knock, diffMs, Victim, 10101, 14_000, 14_000);
        CastWhenReady(ref _shoot, diffMs, RandomThreatTarget(), 16496, 3_000, 4_000);
        CastWhenReady(ref _riflemen, diffMs, Me, 17279, 30_000, 30_000);
    }
}

/// <summary>boss_dathrohan_balnazzarAI (mangos-classic stratholme/boss_dathrohan_balnazzar.cpp: Reset, Aggro, JustDied, UpdateAI).</summary>
public sealed class DathrohanBalnazzarAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _hammer, _crusader, _mindBlast, _holy, _shock, _scream, _sleep, _control;
    private bool _transformed;

    public override void OnRespawn()
    {
        _hammer = 8_000;
        _crusader = 12_000;
        _mindBlast = 6_000;
        _holy = 18_000;
        _shock = 4_000;
        _scream = 16_000;
        _sleep = 20_000;
        _control = 10_000;
        _transformed = false;
        if (Me.Template.Entry == 10813)
        {
            System?.UpdateEntry(Me, 10812);
        }
    }

    public override void OnAggro(Unit target) => System?.SayText(Me, -1329016);
    public override void OnDeath(Unit? killer) => System?.SayText(Me, -1329018);

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _mindBlast, diffMs, Victim, 17287, 15_000, 20_000);
        if (!_transformed)
        {
            CastWhenReady(ref _hammer, diffMs, Victim, 17286, 12_000, 12_000);
            CastWhenReady(ref _crusader, diffMs, Victim, 17281, 15_000, 15_000);
            CastWhenReady(ref _holy, diffMs, Victim, 17284, 15_000, 15_000);
            if ((ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 40 && DoCast(Me, 17288) == CreatureCastResult.Ok)
            {
                System?.UpdateEntry(Me, 10813);
                System?.SayText(Me, -1329017);
                _transformed = true;
            }
        }
        else
        {
            CastWhenReady(ref _shock, diffMs, Victim, 17399, 11_000, 11_000);
            CastWhenReady(ref _scream, diffMs, RandomThreatTarget(), 13704, 20_000, 20_000);
            CastWhenReady(ref _sleep, diffMs, RandomThreatTarget(), 12098, 15_000, 15_000);
            CastWhenReady(ref _control, diffMs, Victim, 15690, 15_000, 15_000);
        }
    }
}
