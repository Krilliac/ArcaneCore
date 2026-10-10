using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// Princess Huhuran (15509), vmangos boss_huhuran.cpp: 80-yard aggro, Frenzy, Wyvern Sting,
/// Acid Spit, Noxious Poison, and Berserk at 30 percent health or five minutes.
/// </summary>
public sealed class HuhuranAI : RaidBossAI
{
    private uint _frenzy;
    private uint _wyvern;
    private uint _spit;
    private uint _poison;
    private uint _berserkTimer;
    private bool _berserk;
    private bool _berserkApplied;

    public HuhuranAI(Creature creature) : base(creature, TempleOfAhnQirajInstance.Huhuran) => ResetActions();

    protected override void ResetActions()
    {
        base.ResetActions();
        _frenzy = RandomDelay(25_000, 35_000);
        _wyvern = RandomDelay(18_000, 28_000);
        _spit = 8_000;
        _poison = RandomDelay(10_000, 20_000);
        _berserkTimer = 300_000;
        _berserk = false;
        _berserkApplied = false;
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (Victim is null && System is { } system && system.CanAggroOnSight(Me, who, scriptedRange: 80f))
            system.EnterCombatWithTarget(Me, who);
        base.MoveInLineOfSight(who);
    }

    private static bool Due(ref uint timer, uint diffMs)
    {
        timer = timer > diffMs ? timer - diffMs : 0;
        return timer == 0;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Due(ref _frenzy, diffMs) && Me.Map?.Combat.SpellMitigation?.HasAura(Me, 26051) != true
            && Cast(26051, Me))
        {
            System?.SayText(Me, 7797);
            _frenzy = RandomDelay(25_000, 35_000);
        }

        if (!_berserk && Due(ref _wyvern, diffMs) && Cast(26180, Me))
            _wyvern = RandomDelay(15_000, 32_000);

        if (Due(ref _spit, diffMs) && Cast(26050, Victim))
            _spit = RandomDelay(5_000, 10_000);

        if (Due(ref _poison, diffMs) && RandomTarget() is { } target && Cast(26053, target))
            _poison = RandomDelay(12_000, 24_000);

        if (_berserk)
        {
            // Keep the permanent aura up after dispel; the fake caster used by tests has no aura store.
            if ((!_berserkApplied || Me.Map?.Combat.SpellMitigation is { } spells && !spells.HasAura(Me, 26068))
                && Cast(26068, Me))
                _berserkApplied = true;
        }
        else if (HealthBelowPct(31) || Due(ref _berserkTimer, diffMs))
        {
            System?.SayText(Me, 4428);
            _berserk = true;
        }
    }
}
