using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Instances.Scripts.Stratholme;

/// <summary>boss_baroness_anastariAI (mangos-classic stratholme/boss_baroness_anastari.cpp: Reset, EnterEvadeMode, UpdateAI, and the
/// AnastariPossess aura script).</summary>
public sealed class BaronessAnastariAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellPossess = 17244, SpellPossessed = 17246, SpellPossessInvisibility = 17250;
    private uint _wail, _curse, _silence, _possess, _possessEnd;
    private Player? _possessed;
    private bool _possessApplied;

    /// <summary>Whether the Baroness is possessing a player (m_uiPossessEndTimer is running).</summary>
    public bool IsPossessing => _possessEnd != 0;

    public override void OnRespawn()
    {
        _wail = 0;
        _curse = 10_000;
        _silence = 25_000;
        _possess = 15_000;
        _possessEnd = 0;
        _possessed = null;
        _possessApplied = false;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_possessEnd != 0)
        {
            // UpdateAI's possess check, every second: the possession ends when her invisibility is gone, when the player died, or when the
            // player is at or below half health (then Possess and Possessed are taken off the player too). While it runs she does not
            // fight - nor evade (EnterEvadeMode returns early while m_uiPossessEndTimer runs): the victim update is skipped.
            if (_possessEnd > diffMs)
            {
                _possessEnd -= diffMs;
                return;
            }

            _possessEnd = 1_000;
            CheckPossession();
            if (_possessEnd != 0)
            {
                return;
            }
        }

        if (!InCombat())
        {
            return;
        }

        CastWhenReady(ref _wail, diffMs, RandomThreatTarget(), 16565, 2_000, 3_000);
        CastWhenReady(ref _curse, diffMs, Me, 16867, 20_000, 20_000);
        CastWhenReady(ref _silence, diffMs, RandomThreatTarget(), 18327, 25_000, 25_000);
        Unit? possessTarget = RandomThreatTarget(skipVictim: true, playerOnly: true);
        if (CastWhenReady(ref _possess, diffMs, possessTarget, SpellPossess, 30_000, 30_000) && possessTarget is not null)
        {
            _possessed = possessTarget as Player;
            _possessEnd = 1_000;
            _possessApplied = false;
        }
    }

    private void CheckPossession()
    {
        CreatureMapSystem? system = System;
        // GetMap()->GetPlayer(m_possessedPlayer): the player, while still in her map.
        Player? player = _possessed is { } possessed && ReferenceEquals(possessed.Map, Me.Map) ? possessed : null;
        if (system is null)
        {
            End();
            return;
        }

        if (!_possessApplied)
        {
            // AnastariPossess::OnApply: once Possess is on the player, she casts Possessed on the player and her invisibility on herself.
            if (player is { IsAlive: true } && system.HasAura(player, SpellPossess))
            {
                _possessApplied = true;
                system.CastSpell(Me, SpellPossessed, player, triggered: true);
                DoCast(Me, SpellPossessInvisibility, triggered: true);
                return;
            }

            if (player is { IsAlive: true } && (system.AiServices.Spells?.IsCasting(Me) ?? false))
            {
                return; // the Possess cast is still going
            }

            End();
            return;
        }

        if (!system.HasAura(Me, SpellPossessInvisibility))
        {
            End();
            return;
        }

        if (player is not { IsAlive: true })
        {
            system.RemoveAuras(Me, SpellPossessInvisibility);
            End();
            return;
        }

        if ((ulong)player.Health * 2 <= player.MaxHealth)
        {
            system.RemoveAuras(Me, SpellPossessInvisibility);
            system.RemoveAuras(player, SpellPossessed);
            system.RemoveAuras(player, SpellPossess);
            End();
        }

        void End()
        {
            _possessEnd = 0;
            _possessed = null;
            _possessApplied = false;
        }
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
        // SelectAttackingTarget(RANDOM, 0, SPELL_DRAIN_MANA, SELECT_FLAG_POWER_MANA) (boss_maleki_the_pallid.cpp:103): mana users only.
        CastWhenReady(ref _mana, diffMs, RandomThreatTarget(filter: u => u.PowerType == PowerType.Mana), 17243, 20_000, 30_000);
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
        // SelectAttackingTarget(RANDOM, 0, SPELL_SHOOT, SELECT_FLAG_NOT_IN_MELEE_RANGE) (boss_cannon_master_willey.cpp:86).
        CastWhenReady(ref _shoot, diffMs, RandomThreatTarget(filter: u => !MapCombat.CanReachWithMeleeAutoAttack(Me, u)), 16496, 3_000, 4_000);
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
