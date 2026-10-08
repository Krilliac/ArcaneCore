using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Scholomance;

/// <summary>boss_darkmaster_gandlingAI (mangos-classic scholomance/boss_darkmaster_gandling.cpp: Reset, UpdateAI).</summary>
public sealed class DarkmasterGandlingAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _missiles, _shield, _curse, _portal;

    public override void OnRespawn()
    {
        _missiles = 4_500;
        _shield = 12_000;
        _curse = 2_000;
        _portal = 16_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _missiles, diffMs, Victim, 15790, 8_000, 8_000);
        CastWhenReady(ref _shield, diffMs, Me, 12040, 14_000, 28_000);
        CastWhenReady(ref _curse, diffMs, Victim, 18702, 15_000, 27_000);
        // SD2 refuses to exile a player when below 3%, so Gandling remains lootable.
        if ((ulong)Me.Health * 100 > (ulong)Me.MaxHealth * 3)
        {
            Unit? player = RandomThreatTarget(playerOnly: true);
            if (CastWhenReady(ref _portal, diffMs, player, 17950, 20_000, 35_000) && player is not null)
            {
                Me.Combat.Threat.Remove(player); // -100% threat after the shadow portal
            }
        }
    }
}

/// <summary>boss_jandicebarovAI (mangos-classic scholomance/boss_jandice_barov.cpp: Reset, JustSummoned, UpdateAI).</summary>
public sealed class JandiceBarovAI(Creature creature) : ScriptDevBossAI(creature)
{
    private uint _curse, _illusion, _banish;

    public override void OnRespawn()
    {
        _curse = 5_000;
        _illusion = 15_000;
        _banish = (uint)(System?.RandomInt(9_000, 13_000) ?? 9_000);
    }

    public override void OnJustSummoned(Creature summoned)
        => summoned.System?.CastSpell(summoned, 17772, summoned, triggered: true);

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _curse, diffMs, Me, 16098, 30_000, 35_000);
        CastWhenReady(ref _banish, diffMs, RandomThreatTarget(skipVictim: true), 8994, 17_000, 21_000);
        if (CastWhenReady(ref _illusion, diffMs, Me, 17773, 25_000, 25_000))
        {
            DoCast(Me, 17774);
        }
    }
}
