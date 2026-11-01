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

/// <summary>The fixed-interval timer of the Stratholme boss scripts: the timer restarts whether or not the spell went off, unlike
/// <see cref="ScriptDevBossAI.CastWhenReady"/> which retries a failed cast.</summary>
internal static class StratholmeTimers
{
    /// <summary>Counts <paramref name="timer"/> down; true (and the timer set to <paramref name="resetMs"/>) when it ran out this tick.</summary>
    public static bool Elapsed(ref uint timer, uint diffMs, uint resetMs)
    {
        if (timer > diffMs)
        {
            timer -= diffMs;
            return false;
        }

        timer = resetMs;
        return true;
    }
}

/// <summary>boss_ramstein_the_gorgerAI (vmangos stratholme/boss_ramstein_the_gorger.cpp: Reset, UpdateAI). The encounter state (aggro, fail,
/// death) is kept by <see cref="StratholmeInstance"/> through its creature hooks.</summary>
public sealed class RamsteinTheGorgerAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellTrample = 5568, SpellKnockout = 17307;
    private uint _trample, _knockout;

    public override void OnRespawn()
    {
        _trample = 3_000;
        _knockout = 12_000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (StratholmeTimers.Elapsed(ref _trample, diffMs, 7_000))
        {
            DoCast(Me, SpellTrample);
        }

        if (_knockout > diffMs)
        {
            _knockout -= diffMs;
            return;
        }

        // Knockout drops the victim's threat to nothing once it landed (modifyThreatPercent(victim, -100)); a failed cast tries again next tick.
        Unit? victim = Victim;
        if (victim is not null && DoCast(victim, SpellKnockout) == CreatureCastResult.Ok)
        {
            Me.Combat.Threat.ModifyThreatPercent(victim, -100);
            _knockout = 10_000;
        }
        else
        {
            _knockout = 0;
        }
    }
}

/// <summary>boss_magistrate_barthilasAI (vmangos stratholme/boss_magistrate_barthilas.cpp: Reset, MoveInLineOfSight, JustDied, UpdateAI).</summary>
public sealed class MagistrateBarthilasAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellDrainingBlow = 16793, SpellCrowdPummel = 10887, SpellMightyBlow = 14099, SpellFuriousAnger = 16791;
    public const uint ModelNormal = 10433, ModelHuman = 3637;
    public const uint MaxFuriousAnger = 25;
    private uint _draining, _pummel, _mighty, _anger, _angerCount;

    /// <summary>How many Furious Anger casts he has made this fight.</summary>
    public uint AngerCount => _angerCount;

    public override void OnRespawn()
    {
        _draining = 16_000;
        _pummel = 12_000;
        _mighty = 8_000;
        _anger = 5_000;
        _angerCount = 0;
        Me.DisplayId = Me.IsAlive ? ModelNormal : ModelHuman; // the undead model while alive, the human one as a corpse
    }

    public override void OnDeath(Unit? killer) => Me.DisplayId = ModelHuman;

    public override void MoveInLineOfSight(Unit who)
    {
        // A player within 10 yards lifts the spawning flag (he starts out unattackable until the run is over).
        if (who is Player && (Me.UnitFlags & UnitFlags.Spawning) != 0 && WorldGeometry.CenterDistance3D(Me, who) <= 10f)
        {
            Me.UnitFlags &= ~UnitFlags.Spawning;
        }

        base.MoveInLineOfSight(who);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (StratholmeTimers.Elapsed(ref _anger, diffMs, 4_000))
        {
            // Past 25 casts the whole update stops (the script returns before the other timers).
            if (_angerCount > MaxFuriousAnger)
            {
                return;
            }

            _angerCount++;
            DoCast(Me, SpellFuriousAnger);
        }

        if (StratholmeTimers.Elapsed(ref _draining, diffMs, 15_000))
        {
            DoCast(Victim, SpellDrainingBlow);
        }

        if (StratholmeTimers.Elapsed(ref _pummel, diffMs, 15_000))
        {
            DoCast(Victim, SpellCrowdPummel);
        }

        if (StratholmeTimers.Elapsed(ref _mighty, diffMs, 20_000))
        {
            DoCast(Victim, SpellMightyBlow);
        }
    }
}

/// <summary>boss_nerubenkanAI (vmangos stratholme/boss_nerubenkan.cpp: Reset, RaiseUndeadScarab, UpdateAI). The encounter state is kept by
/// <see cref="StratholmeInstance"/>.</summary>
public sealed class NerubenkanAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellEncasingWebs = 4962, SpellPierceArmor = 6016, NpcCryptScarab = 10577, NpcUndeadScarab = 10876;
    private const float SpawnRadius = 10f;
    private const uint ScarabLifetimeMs = 10_000;
    private uint _webs, _pierce, _raise;
    private Unit? _webbed;
    private float _webbedThreat;

    public override void OnRespawn()
    {
        _webs = 7_000;
        _pierce = 15_000;
        _raise = 3_000;
        ForgetWebbed();
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        if (_webbed is not null)
        {
            // The webbed player's threat was wiped; it comes back once the web is off (a player who left the map is forgotten).
            if (ReferenceEquals(_webbed.Map, Me.Map))
            {
                if (System?.HasAura(_webbed, SpellEncasingWebs) != true)
                {
                    Me.Combat.Threat.AddThreat(_webbed, _webbedThreat);
                    ForgetWebbed();
                }
            }
            else
            {
                ForgetWebbed();
            }
        }

        Unit? victim = Victim;
        if (CastWhenReady(ref _webs, diffMs, victim, SpellEncasingWebs, 10_000, 15_000) && victim is not null)
        {
            _webbed = victim;
            _webbedThreat = Me.Combat.Threat.GetThreat(victim);
            Me.Combat.Threat.ModifyThreatPercent(victim, -100);
        }

        CastWhenReady(ref _pierce, diffMs, Victim, SpellPierceArmor, 15_000, 20_000);

        if (_raise > diffMs)
        {
            _raise -= diffMs;
        }
        else
        {
            _raise = 0;
            if (RandomThreatTarget() is { } target)
            {
                RaiseUndeadScarab(target, (System?.RandomInt(0, 1) ?? 0) != 0);
                _raise = (uint)(System?.RandomInt(6_000, 10_000) ?? 6_000);
            }
        }
    }

    private void ForgetWebbed()
    {
        _webbed = null;
        _webbedThreat = 0;
    }

    /// <summary>RaiseUndeadScarab: the crypt version raises 4, 6 or 8 Crypt Scarabs, the other one Undead Scarab; they spawn on a random point
    /// within 10 yards of him, fight <paramref name="victim"/> and go 10 s after they are out of combat.</summary>
    private void RaiseUndeadScarab(Unit victim, bool crypt)
    {
        if (System is not { } system)
        {
            return;
        }

        int amount = crypt ? 4 + (2 * system.RandomInt(0, 2)) : 1;
        uint entry = crypt ? NpcCryptScarab : NpcUndeadScarab;
        for (int i = 0; i < amount; i++)
        {
            double angle = system.RandomInt(0, 35_999) * (Math.PI / 18_000d);
            double distance = SpawnRadius * Math.Sqrt(system.RandomInt(0, 10_000) / 10_000d);
            float x = Me.X + (float)(distance * Math.Cos(angle)), y = Me.Y + (float)(distance * Math.Sin(angle));
            float z = Me.Map is { } map && MapCreatureHeightProvider.Floor(map, x, y, Me.Z + 2f) is { } floor && Math.Abs(floor - Me.Z) <= 10f
                ? floor : Me.Z;
            float facing = (float)(Math.Atan2(y - Me.Y, x - Me.X) + Math.PI);
            system.SummonAt(Me, entry, x, y, z, facing, victim, ScarabLifetimeMs);
        }
    }
}

/// <summary>boss_postmaster_malownAI (vmangos stratholme/boss_postmaster_malown.cpp: Reset, Aggro, KilledUnit, UpdateAI). Each spell rolls
/// its chance when the timer is up.</summary>
public sealed class PostmasterMalownAI(Creature creature) : ScriptDevBossAI(creature)
{
    public const uint SpellWailingDead = 7713, SpellBackhand = 6253, SpellCurseOfWeakness = 8552, SpellCurseOfTongues = 12889,
        SpellCallOfTheGrave = 17831;
    public const int SayAggro = 6504, SayKilled = 6530; // broadcast_text ids
    private uint _wailing, _backhand, _weakness, _tongues, _grave;

    public override void OnRespawn()
    {
        _wailing = 19_000;
        _backhand = 8_000;
        _weakness = 20_000;
        _tongues = 22_000;
        _grave = 25_000;
    }

    public override void OnAggro(Unit target) => System?.SayText(Me, SayAggro);

    public override void OnKilledUnit(Unit victim) => System?.SayText(Me, SayKilled);

    public override void OnUpdate(uint diffMs)
    {
        if (!InCombat())
        {
            return;
        }

        Roll(ref _wailing, diffMs, 19_000, 65, SpellWailingDead);
        Roll(ref _backhand, diffMs, 8_000, 45, SpellBackhand);
        Roll(ref _weakness, diffMs, 20_000, 3, SpellCurseOfWeakness);
        Roll(ref _tongues, diffMs, 22_000, 3, SpellCurseOfTongues);
        Roll(ref _grave, diffMs, 25_000, 5, SpellCallOfTheGrave);
    }

    // rand() % 100 < chance, at the victim; the timer restarts either way.
    private void Roll(ref uint timer, uint diffMs, uint resetMs, int chancePercent, uint spell)
    {
        if (StratholmeTimers.Elapsed(ref timer, diffMs, resetMs) && (System?.RandomInt(0, 99) ?? 100) < chancePercent)
        {
            DoCast(Victim, spell);
        }
    }
}
