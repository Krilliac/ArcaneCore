using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using static ArcaneCore.Game.Battlegrounds.AlteracValley;

namespace ArcaneCore.Game.Battlegrounds.AlteracValleyScripts;

/// <summary>
/// vmangos FrostwolfShamanAI and DruidOfTheGroveAI (scripts/battlegrounds/battleground_alterac.cpp:4355-4493), the adds of Primalist
/// Thurloga (scripts npc_frostwolf_shaman) and Arch Druid Renferal (npc_druid_of_the_grove): a fight takes them off their mount and out of
/// their channel, which they take up again (mount and channel) when it is over; the shaman keeps Lightning Shield up and casts Frost Shock
/// and Healing Wave, the druid keeps Thorns and casts Entangling Roots and Starfire.
/// </summary>
public sealed class AvSummonerAddAI : CreatureAI
{
    public const uint SpellFrostShock = 21401;
    public const uint SpellLightningShield = 12550;
    public const uint SpellThorns = 22128;

    private readonly AlteracValleyScripts _scripts;
    private bool _usingMount;
    private bool _channeling;
    private uint _frostShockTimer;
    private uint _healingWaveTimer;
    private uint _lightningShieldTimer;
    private uint _entanglingRootsTimer;
    private uint _starfireTimer;

    public AvSummonerAddAI(Creature creature, AlteracValleyScripts scripts)
        : base(creature)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        _scripts = scripts;
    }

    private bool IsShaman => Me.Template.Entry == NpcFrostwolfShaman;

    private Random Rng => _scripts.Match.ScriptRandom;

    public override bool AggroesOnSight => !Me.Template.Civilian;

    public override void OnRespawn() => Reset();

    public override void OnEvade() => Reset();

    /// <summary>Reset (:4372-4381, 4446-4455).</summary>
    private void Reset()
    {
        if (IsShaman)
        {
            DoCast(Me, SpellLightningShield);
            _frostShockTimer = 5000;
            _healingWaveTimer = 0;
        }
        else
        {
            DoCast(Me, SpellThorns);
            _entanglingRootsTimer = 3000;
            _starfireTimer = 0;
        }

        if (_usingMount)
        {
            AvScript.Mount(Me, IsShaman ? 1166u : 9695u);
        }

        if (_channeling)
        {
            DoCast(Me, SpellInvocation);
        }
    }

    /// <summary>EnterCombat (:4383-4394, 4457-4468).</summary>
    public override void OnAggro(Unit target)
    {
        _usingMount = AvScript.IsMounted(Me);
        if (System?.HasAura(Me, SpellInvocation) == true)
        {
            _channeling = true;
            System.InterruptCast(Me);
        }
        else
        {
            _channeling = false;
        }

        AvScript.Unmount(Me);
    }

    /// <summary>UpdateAI (:4396-4428, 4470-4491).</summary>
    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim() || Victim is not { } victim)
        {
            return;
        }

        if (IsShaman)
        {
            if (_frostShockTimer < diffMs)
            {
                if (DoCast(victim, SpellFrostShock) == CreatureCastResult.Ok)
                {
                    _frostShockTimer = AvScript.URand(Rng, 5000, 6000);
                }
            }
            else
            {
                _frostShockTimer -= diffMs;
            }

            if (_healingWaveTimer < diffMs)
            {
                if (Me.MaxHealth > 0 && Me.Health * 100f / Me.MaxHealth < 70.0f)
                {
                    DoCast(Me, AvEventAI.SpellHealingWave);
                    _healingWaveTimer = 7000;
                }
            }
            else
            {
                _healingWaveTimer -= diffMs;
            }

            if (_lightningShieldTimer < diffMs && System?.HasAura(Me, SpellLightningShield) != true)
            {
                DoCast(Me, SpellLightningShield);
                _lightningShieldTimer = AvScript.URand(Rng, 9000, 11000);
            }
            else if (_lightningShieldTimer >= diffMs)
            {
                _lightningShieldTimer -= diffMs;
            }

            return;
        }

        if (_entanglingRootsTimer < diffMs)
        {
            if (DoCast(victim, AvEventAI.SpellEntanglingRoots) == CreatureCastResult.Ok)
            {
                _entanglingRootsTimer = AvScript.URand(Rng, 9000, 13000);
            }
        }
        else
        {
            _entanglingRootsTimer -= diffMs;
        }

        if (_starfireTimer < diffMs)
        {
            DoCast(victim, AvEventAI.SpellStarfire);
            _starfireTimer = AvScript.URand(Rng, 7000, 9500);
        }
        else
        {
            _starfireTimer -= diffMs;
        }
    }
}
