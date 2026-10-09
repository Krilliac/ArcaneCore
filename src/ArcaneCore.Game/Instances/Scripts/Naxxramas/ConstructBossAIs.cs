using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// vmangos naxxramas/boss_patchwerk.cpp boss_patchwerkAI::{Aggro,DoHatefulStrike,UpdateAI}:
/// first four threat entries in melee, highest current health other than the tank, 1.2 s strike,
/// 5% soft enrage and 7-minute hard enrage. mangos-classic boss_patchwerk.cpp concurs.
/// </summary>
public sealed class PatchwerkAI : RaidBossAI
{
    private bool _softEnrage;

    public PatchwerkAI(Creature creature) : base(creature, NaxxramasInstance.Patchwerk)
    {
        AddAction(1200, HatefulStrike, () => 1200);
        AddAction(420000, () => Cast(26662, Me), () => 300000);
        AddAction(450000, () => Cast(32309, Me), () => 5000);
    }

    protected override void ResetActions() { base.ResetActions(); _softEnrage = false; }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_softEnrage && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 5 && Cast(28131, Me))
            _softEnrage = true;
        base.UpdateCombat(diffMs);
    }

    private bool HatefulStrike()
    {
        Unit? tank = Victim;
        if (tank is null) return false;
        // vmangos DoHatefulStrike: walk the threat list counting only players in melee reach (the tank
        // included), stop after four, and take the highest current health among them other than the tank.
        Unit? target = Me.Combat.Threat.Entries
            .Select(e => e.Target)
            .OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map)
            .Where(p => { float x = p.X - Me.X, y = p.Y - Me.Y; return x * x + y * y <= 25; })
            .Take(4)
            .Where(p => p != tank)
            .OrderByDescending(p => p.Health)
            .FirstOrDefault();
        return Cast(28308, target ?? tank);
    }
}

/// <summary>
/// vmangos naxxramas/boss_grobbulus.cpp boss_grobbulusAI::{Aggro,DoCastMutagenInjection,
/// SpellHitTarget,UpdateAI}; mangos-classic boss_grobbulus.cpp MutatingInjection::OnApply.
/// </summary>
public sealed class GrobbulusAI : RaidBossAI
{
    private uint _slimeStream = 5000;

    public GrobbulusAI(Creature creature) : base(creature, NaxxramasInstance.Grobbulus)
    {
        AddAction(12000, Inject, () => (ulong)Me.Health * 100 > (ulong)Me.MaxHealth * 30
            ? RandomDelay(7000, 13000) : RandomDelay(3000, 7000));
        AddAction(15000, () => Cast(28240, Me), () => 15000);
        AddAction(20000, () => Cast(28157, Victim), () => RandomDelay(30000, 35000));
        AddAction(720000, () => Cast(26662, Me), () => 300000);
    }

    private bool Inject()
    {
        Unit[] eligible = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .OfType<Player>().Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map && System?.HasAura(p, 28169) != true)];
        if (eligible.Length == 0) return false;
        return Cast(28169, eligible[System!.RandomInt(0, eligible.Length - 1)]);
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 28157 && target is Player) Cast(28218, target, triggered: true);
    }

    protected override void ResetActions() { base.ResetActions(); _slimeStream = 5000; }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Victim is { } victim)
        {
            float x = victim.X - Me.X, y = victim.Y - Me.Y;
            if (x * x + y * y > 25)
            {
                if (_slimeStream > diffMs) _slimeStream -= diffMs;
                else if (Cast(28137, Me, triggered: true)) _slimeStream = 1500;
            }
            else _slimeStream = 5000;
        }
        base.UpdateCombat(diffMs);
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_gluth.cpp boss_gluthAI::{Aggro,ExecuteAction,StopSummoning}
/// and Decimate; vmangos boss_gluth.cpp confirms spell IDs and 105 s first Decimate.
/// </summary>
public sealed class GluthAI : RaidBossAI
{
    public GluthAI(Creature creature) : base(creature, NaxxramasInstance.Gluth)
    {
        AddAction(10000, () => Cast(25646, Victim), () => 10000);
        AddAction(105000, Decimate, () => RandomDelay(100000, 110000));
        AddAction(10000, () => Cast(28371, Me), () => RandomDelay(10000, 12000));
        AddAction(20000, () => Cast(29685, Me), () => 20000);
        AddAction(390000, () => Cast(26662, Me), () => 300000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(19818, Me, triggered: true);
        Cast(28235, Me, triggered: true);
        Cast(29681, Me, triggered: true);
        if (System is { } system)
            foreach (Creature trigger in ZombieTriggers())
                system.CastSpell(trigger, 28216, trigger, triggered: true);
    }

    public override void OnDeath(Unit? killer)
    {
        StopZombies();
        base.OnDeath(killer);
    }

    public override void OnReachedHome()
    {
        StopZombies();
        base.OnReachedHome();
    }

    private IEnumerable<Creature> ZombieTriggers()
        => System?.Creatures.Where(c => c.Entry == 15384
            && (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) < 10000) ?? [];

    private void StopZombies()
    {
        if (System is not { } system) return;
        foreach (Creature trigger in ZombieTriggers()) system.RemoveAuras(trigger, 28216);
    }

    private bool Decimate()
    {
        if (!Cast(28374, Me)) return false;
        // SD2 Decimate::OnEffectExecute: players, player-controlled pets and Zombie Chow only.
        if (Me.Map is not null)
        {
            foreach (Player player in Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
                .Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map))
                player.Health = Math.Min(player.Health, Math.Max(1u, player.MaxHealth / 20));
            if (System is { } system)
                foreach (Creature zombie in system.Creatures.Where(c => c.Entry == 16360 && c.IsAlive
                    && (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) < 40000))
                    zombie.Health = Math.Min(zombie.Health, Math.Max(1u, zombie.MaxHealth / 20));
        }
        return true;
    }
}

/// <summary>
/// vmangos naxxramas/boss_thaddius.cpp boss_thaddiusAI::{StartPhase2,UpdateAI};
/// mangos-classic boss_thaddius.cpp boss_thaddiusAddsAI::{Aggro,Revive,ExecuteAction}.
/// The instance waits for both add deaths and starts this AI's phase two after the Tesla delay.
/// </summary>
public sealed class ThaddiusAI : RaidBossAI
{
    private uint _ballLightning = 1000;
    public ThaddiusAI(Creature creature) : base(creature, NaxxramasInstance.Thaddius)
    {
        AddAction(30000, () => Cast(28089, Me), () => 30000);
        AddAction(15000, () => Cast(28167, Victim), () => RandomDelay(15000, 20000));
        AddAction(360000, () => Cast(27680, Me), () => 300000);
    }

    public override void OnAggro(Unit target)
    {
        if (Instance is NaxxramasInstance { ConstructAddsDefeated: false })
        {
            EnterEvadeMode();
            return;
        }
        base.OnAggro(target);
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        if (Instance is NaxxramasInstance { ConstructAddsDefeated: false })
            Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
    }

    protected override void ResetActions() { base.ResetActions(); _ballLightning = 1000; }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Victim is { } victim)
        {
            float x = victim.X - Me.X, y = victim.Y - Me.Y;
            if (x * x + y * y > 25)
            {
                if (_ballLightning > diffMs) _ballLightning -= diffMs;
                else if (Cast(28299, victim)) _ballLightning = 1500;
            }
            else _ballLightning = 1000;
        }
        base.UpdateCombat(diffMs);
    }
}

public sealed class ThaddiusAddAI : RaidBossAI
{
    private bool _fakingDeath;

    public ThaddiusAddAI(Creature creature) : base(creature, NaxxramasInstance.Thaddius)
    {
        AddAction(10000, () => Cast(creature.Entry == 15929 ? 28125u : 28135u, Victim), () => 10000);
        if (creature.Entry == 15930) AddAction(30000, MagneticPull, () => 30000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        uint otherEntry = Me.Entry == 15929 ? 15930u : 15929u;
        Creature? other = System?.Creatures.FirstOrDefault(c => c.Entry == otherEntry && c.IsAlive);
        if (other is not null && !other.Combat.IsInCombat) other.AI?.AttackStart(target);
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        Revive();
    }

    public override void OnAttackedBy(Unit attacker)
    {
        // The core's death-prevention threshold clamps a lethal hit to one HP.
        // SD2 boss_thaddiusAddsAI::DamageTaken then holds the add in fake death.
        if (!_fakingDeath && Me.Health <= 1)
        {
            _fakingDeath = true;
            Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer;
            MeleeEnabled = false;
            System?.StopMoving(Me);
            (Instance as NaxxramasInstance)?.RecordConstructAddDeath(Me.Entry);
        }
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!_fakingDeath) base.OnUpdate(diffMs);
    }

    internal void Revive()
    {
        _fakingDeath = false;
        Me.Health = Me.MaxHealth;
        Me.InvincibilityHpThreshold = 1;
        Me.UnitFlags &= ~(UnitFlags.NotSelectable | UnitFlags.ImmuneToPlayer);
        MeleeEnabled = true;
    }

    private bool MagneticPull()
    {
        // vmangos boss_thaddius.cpp boss_thaddiusAddsAI::DoMagneticPull:
        // each add casts on the opposite tank. The spell's DBC effects perform the jump.
        Creature? other = System?.Creatures.FirstOrDefault(c => c.Entry == 15929 && c.IsAlive);
        Unit? ownTank = Victim;
        Unit? otherTank = other?.Combat.Threat.Entries.FirstOrDefault()?.Target;
        if (other is null || ownTank is null || otherTank is null || ownTank == otherTank) return false;
        bool first = Cast(28337, otherTank, triggered: true);
        bool second = System?.CastSpell(other, 28337, ownTank, triggered: true) == CreatureCastResult.Ok;
        return first && second;
    }

    public override void OnDeath(Unit? killer) => (Instance as NaxxramasInstance)?.RecordConstructAddDeath(Me.Entry);
}
