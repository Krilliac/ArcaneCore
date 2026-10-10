using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.RuinsOfAhnQiraj;

/// <summary>
/// mangos-classic kalimdor/ruins_of_ahnqiraj/ruins_of_ahnqirajScripts.cpp mob_anubisath_guardianAI (15355).
/// Reset picks one of each pair: Meteor/Plague, Shadow Storm/Thunder Clap, the two reflects, Enrage/Explode and the two summons.
/// Aggro casts the reflect. Timers: spell 1 at 10 s, spell 2 at 20 s, the summon at 10 s, each repeating every 15 s; the summon is
/// skipped while four summons are up. Below 10% it enrages once (with EMOTE_FRENZY, broadcast_text 1191 as in <see
/// cref="BlackwingLair.FlamegorAI"/>) or keeps trying Explode at the victim. Limit: the reference checks the 10% in DamageTaken, this
/// checks it on the next update.
/// </summary>
public sealed class AnubisathGuardianAI : RaidBossAI
{
    public const uint Entry = 15355;
    public const uint Meteor = 24340, Plague = 22997, ShadowStorm = 26546, ThunderClap = 26554;
    public const uint ReflectArcaneFire = 13022, ReflectFrostShadow = 19595, Enrage = 8599, Explode = 25698;
    public const uint SummonSwarmguard = 17430, SummonWarrior = 17431;

    private bool _enraged;

    public AnubisathGuardianAI(Creature creature) : base(creature, null)
    {
        Roll();
        AddAction(10000, () => Cast(Spell1, Victim), () => 15000);
        AddAction(20000, () => Cast(Spell2, Victim), () => 15000);
        // The summon timer restarts at 15 s whether or not it cast (the count gate is inside the timer).
        AddAction(10000, () => { if (SummonCount < 4) Cast(Spell5, Victim); return true; }, () => 15000);
    }

    public uint Spell1 { get; private set; }
    public uint Spell2 { get; private set; }
    public uint Spell3 { get; private set; }
    public uint Spell4 { get; private set; }
    public uint Spell5 { get; private set; }
    public int SummonCount { get; private set; }

    private bool Coin() => (System?.RandomInt(0, 1) ?? 0) == 1;

    private void Roll()
    {
        Spell1 = Coin() ? Meteor : Plague;
        Spell2 = Coin() ? ShadowStorm : ThunderClap;
        Spell3 = Coin() ? ReflectArcaneFire : ReflectFrostShadow;
        Spell4 = Coin() ? Enrage : Explode;
        Spell5 = Coin() ? SummonSwarmguard : SummonWarrior;
        SummonCount = 0;
        _enraged = false;
    }

    protected override void ResetActions()
    {
        Roll();
        base.ResetActions();
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(Spell3, Me);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (Victim is { } victim) summoned.AI?.AttackStart(victim);
        SummonCount++;
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        if (SummonCount > 0) SummonCount--;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_enraged && HealthBelowPct(10))
        {
            if (Spell4 == Enrage)
            {
                System?.SayText(Me, 1191);
                Cast(Enrage, Me);
                _enraged = true;
            }
            else Cast(Explode, Victim);
        }

        base.UpdateCombat(diffMs);
    }
}
