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

/// <summary>
/// boss_rasfrostAI (vmangos western_plaguelands/scholomance/boss_ras_frostwhisper.cpp: Reset, UpdateAI): Ice Armor on himself at reset and
/// again every 3 minutes, Frostbolt on a random target, Freeze, Fear, Chill Nova and Frost Volley on the victim.
/// </summary>
public sealed class RasFrostwhisperAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellFrostbolt = 21369, SpellIceArmor = 18100, SpellFreeze = 18763, SpellFear = 26070,
        SpellChillNova = 18099, SpellFrostVolley = 8398;

    private uint _iceArmor, _frostbolt, _freeze, _fear, _chillNova, _frostVolley;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _iceArmor = 2_000;
        _frostbolt = 8_000;
        _chillNova = 12_000;
        _freeze = 18_000;
        _frostVolley = 24_000;
        _fear = 45_000;
        DoCast(Me, SpellIceArmor, triggered: true);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _iceArmor, diffMs, Me, SpellIceArmor, 180_000, 180_000);
        CastWhenReady(ref _frostbolt, diffMs, RandomThreatTarget(), SpellFrostbolt, 8_000, 8_000);
        CastWhenReady(ref _freeze, diffMs, Victim, SpellFreeze, 24_000, 24_000);
        CastWhenReady(ref _fear, diffMs, Victim, SpellFear, 30_000, 30_000);
        CastWhenReady(ref _chillNova, diffMs, Victim, SpellChillNova, 14_000, 14_000);
        CastWhenReady(ref _frostVolley, diffMs, Victim, SpellFrostVolley, 15_000, 15_000);
    }
}

/// <summary>
/// boss_instructormaliciaAI (vmangos scholomance/boss_instructor_malicia.cpp: Reset, UpdateAI): Call of the Grave on the victim, Corruption
/// on a random target, Renew on herself, a burst of three Flash Heals (5 s apart, then 30 s) and Healing Touch. The source's Healing Touch
/// test reads <c>HealingTouch_Timer &lt; 2</c> (the timer, not the counter, so the 5.5 s repeat never happens): she casts it every 30 s.
/// Her death is recorded by ScholomanceInstance.
/// </summary>
public sealed class InstructorMaliciaAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellCallOfGraves = 17831, SpellCorruption = 11672, SpellFlashHeal = 10917, SpellRenew = 10929,
        SpellHealingTouch = 9889;

    private uint _callOfGraves, _corruption, _flashHeal, _renew, _healingTouch;
    private int _flashCounter;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _callOfGraves = 4_000;
        _corruption = 8_000;
        _flashHeal = 22_000;
        _renew = 15_000;
        _healingTouch = 25_000;
        _flashCounter = 0;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _callOfGraves, diffMs, Victim, SpellCallOfGraves, 65_000, 65_000);
        CastWhenReady(ref _corruption, diffMs, RandomThreatTarget(), SpellCorruption, 24_000, 24_000);
        CastWhenReady(ref _renew, diffMs, Me, SpellRenew, 10_000, 10_000);

        int flashNext = _flashCounter < 2 ? 5_000 : 30_000;
        if (CastWhenReady(ref _flashHeal, diffMs, Me, SpellFlashHeal, flashNext, flashNext))
        {
            _flashCounter = _flashCounter < 2 ? _flashCounter + 1 : 0;
        }

        CastWhenReady(ref _healingTouch, diffMs, Me, SpellHealingTouch, 30_000, 30_000);
    }
}

/// <summary>
/// boss_theolenkrastinovAI (vmangos scholomance/boss_doctor_theolen_krastinov.cpp: Reset, UpdateAI): Rend, Backhand (which wipes the
/// victim's threat) and, below 26 percent health, Frenzy with its emote every 2 minutes.
/// </summary>
public sealed class TheolenKrastinovAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellRend = 16509, SpellBackhand = 18103, SpellFrenzy = 8269;
    public const int EmoteGenericFrenzyKill = 7797;

    private uint _rend, _backhand, _frenzy;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _rend = 8_000;
        _backhand = 9_000;
        _frenzy = 1_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _rend, diffMs, Victim, SpellRend, 10_000, 10_000);

        Unit? backhandTarget = Victim;
        if (CastWhenReady(ref _backhand, diffMs, backhandTarget, SpellBackhand, 10_000, 10_000) && backhandTarget is not null)
        {
            Me.Combat.Threat.ModifyThreatPercent(backhandTarget, -100); // DoModifyThreatPercent(victim, -100)
        }

        if (Me.HealthPercent < 26f
            && CastWhenReady(ref _frenzy, diffMs, Me, SpellFrenzy, 120_000, 120_000))
        {
            System?.SayText(Me, EmoteGenericFrenzyKill);
        }
    }
}

/// <summary>
/// boss_lorekeeperpolkeltAI (vmangos scholomance/boss_lorekeeper_polkelt.cpp: Reset, Aggro, UpdateAI): the Dark Plague aura when the fight
/// starts (cast triggered, only when absent), then Volatile Infection, Corrosive Acid and Noxious Catalyst on the victim.
/// </summary>
public sealed class LorekeeperPolkeltAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellVolatileInfection = 24928, SpellDarkPlagueAura = 12038, SpellCorrosiveAcid = 8245,
        SpellNoxiousCatalyst = 18151;

    private uint _volatileInfection, _corrosiveAcid, _noxiousCatalyst;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _volatileInfection = 38_000;
        _corrosiveAcid = 45_000;
        _noxiousCatalyst = 35_000;
    }

    public override void OnAggro(Unit target)
    {
        // CF_TRIGGERED | CF_AURA_NOT_PRESENT
        if (System?.HasAura(Me, SpellDarkPlagueAura) != true)
        {
            DoCast(Me, SpellDarkPlagueAura, triggered: true);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _volatileInfection, diffMs, Victim, SpellVolatileInfection, 32_000, 32_000);
        CastWhenReady(ref _corrosiveAcid, diffMs, Victim, SpellCorrosiveAcid, 25_000, 25_000);
        CastWhenReady(ref _noxiousCatalyst, diffMs, Victim, SpellNoxiousCatalyst, 38_000, 38_000);
    }
}

/// <summary>boss_theravenianAI (vmangos scholomance/boss_the_ravenian.cpp: Reset, UpdateAI): Trample, Cleave, Sundering Cleave and Knock Away on the victim.</summary>
public sealed class TheRavenianAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellTrample = 15550, SpellCleave = 20691, SpellSunderingCleave = 25174, SpellKnockAway = 10101;

    private uint _trample, _cleave, _sunderingCleave, _knockAway;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _trample = 24_000;
        _cleave = 15_000;
        _sunderingCleave = 40_000;
        _knockAway = 32_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _trample, diffMs, Victim, SpellTrample, 10_000, 10_000);
        CastWhenReady(ref _cleave, diffMs, Victim, SpellCleave, 7_000, 7_000);
        CastWhenReady(ref _sunderingCleave, diffMs, Victim, SpellSunderingCleave, 20_000, 20_000);
        CastWhenReady(ref _knockAway, diffMs, Victim, SpellKnockAway, 12_000, 12_000);
    }
}

/// <summary>boss_illuciabarovAI (vmangos scholomance/boss_illucia_barov.cpp: Reset, UpdateAI): Curse of Agony, Silence and Fear on the victim, Shadow Shock on a random target.</summary>
public sealed class IlluciaBarovAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellCurseOfAgony = 18671, SpellShadowShock = 20603, SpellSilence = 15487, SpellFear = 6215;

    private uint _curseOfAgony, _shadowShock, _silence, _fear;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _curseOfAgony = 18_000;
        _shadowShock = 9_000;
        _silence = 5_000;
        _fear = 30_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _curseOfAgony, diffMs, Victim, SpellCurseOfAgony, 30_000, 30_000);
        CastWhenReady(ref _shadowShock, diffMs, RandomThreatTarget(), SpellShadowShock, 12_000, 12_000);
        CastWhenReady(ref _silence, diffMs, Victim, SpellSilence, 14_000, 14_000);
        CastWhenReady(ref _fear, diffMs, Victim, SpellFear, 30_000, 30_000);
    }
}

/// <summary>
/// boss_lordalexeibarovAI (vmangos scholomance/boss_lord_alexei_barov.cpp: Reset, UpdateAI): Immolate on a random target and Veil of Shadow
/// on the victim. The source's reset also reloads the creature_addon auras; the engine applies them on respawn already.
/// </summary>
public sealed class LordAlexeiBarovAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellImmolate = 15570, SpellVeilOfShadow = 17820;

    private uint _immolate, _veilOfShadow;

    public override void OnRespawn() => ResetFight();

    public override void OnEvade() => ResetFight();

    private void ResetFight()
    {
        _immolate = 7_000;
        _veilOfShadow = 15_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _immolate, diffMs, RandomThreatTarget(), SpellImmolate, 12_000, 12_000);
        CastWhenReady(ref _veilOfShadow, diffMs, Victim, SpellVeilOfShadow, 20_000, 20_000);
    }
}
