using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>Ouro's spawner (15957): a player within 25 yards calls the imported summon spell 26061.</summary>
public sealed class OuroSpawnerAI(Creature creature) : CreatureAI(creature)
{
    private bool _summoned;
    public override void OnRespawn()
    {
        _summoned = false;
        DoCast(Me, 26092, triggered: true);
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (_summoned || who is not Player player || player.IsGameMaster || !player.IsAlive) return;
        float dx = player.X - Me.X, dy = player.Y - Me.Y, dz = player.Z - Me.Z;
        if (dx * dx + dy * dy + dz * dz <= 25f * 25f && DoCast(Me, 26061) == CreatureCastResult.Ok)
            _summoned = true;
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry != 15517) return;
        System?.SetInCombatWithZone(summoned);
        if (summoned.AI is { } boss && Me.Map?.Players.Where(p => p.IsAlive && !p.IsGameMaster)
            .OrderBy(p => (p.X - Me.X) * (p.X - Me.X) + (p.Y - Me.Y) * (p.Y - Me.Y))
            .FirstOrDefault() is { } player)
            boss.AttackStart(player);
        if (Me.Map?.Combat.SpellMitigation is { } spells)
            spells.CastSpell(summoned, 26262, SpellCastTargets.ForSelf(), triggered: true);
        System?.ForcedDespawn(Me, 0);
    }

    public override void OnUpdate(uint diffMs) { }
}

/// <summary>Ouro (15517), a stationary worm that alternates ground combat and thirty-second submerges.</summary>
public sealed class OuroAI : RaidBossAI
{
    private const UnitFlags HiddenFlags = UnitFlags.NotSelectable | UnitFlags.Spawning;
    private ObjectGuid _trigger;
    private uint _sweepMs, _sandblastMs, _submergeMs, _noMeleeMs, _graceMs, _moundMs, _invisMs;
    private bool _submerged, _enraged, _summonBase;

    public OuroAI(Creature creature) : base(creature, TempleOfAhnQirajInstance.Ouro) => ResetState();

    private void ResetState()
    {
        _sweepMs = 20_500;
        _sandblastMs = RandomDelay(20_000, 25_000);
        _submergeMs = 90_000; // build 5875 is after the 1.10 timer change
        _noMeleeMs = 3000;
        _graceMs = 10_000;
        _moundMs = 10_000;
        _invisMs = 2000;
        _submerged = _enraged = false;
        _summonBase = true;
        _trigger = default;
        Me.UnitFlags &= ~HiddenFlags;
        SetCombatMovement(false);
        SetMeleeEnabled(true);
    }

    public override void OnRespawn() => ResetState();

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        System?.SetInCombatWithZone(Me);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        switch (summoned.Entry)
        {
            case 15717:
                _trigger = summoned.Guid;
                if (RandomTarget() is { } target) summoned.Motion.MoveFollow(target, 0, 0);
                break;
        }
    }

    public override void OnSummonedCreatureDespawn(Creature summoned)
    {
        if (_trigger == summoned.Guid) _trigger = default;
    }

    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 26102)
            Me.Combat.Threat.ModifyThreatPercent(target, -100);
    }

    public override void OnEvade()
    {
        CleanupAdds(includeScarabs: true);
        ResetState();
    }

    public override void OnReachedHome()
    {
        base.OnReachedHome();
        CleanupAdds(includeScarabs: true);
        Cast(26594, Me, triggered: true);
        System?.ForcedDespawn(Me, 2000);
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        CleanupAdds(includeScarabs: false);
        Cast(26594, Me, triggered: true);
    }

    private void CleanupAdds(bool includeScarabs)
    {
        if (System is not { } system) return;
        // Scarabs are summoned by mounds, so they are not direct summons of Ouro.
        foreach (Creature add in system.Creatures.Where(c => c.Entry == 15712 || includeScarabs && c.Entry == 15718)
            .Where(c => (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) <= 250f * 250f).ToArray())
            system.ForcedDespawn(add, 0);
    }

    private static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }

    private void Submerge()
    {
        if (_submerged || _enraged || !Cast(26063, Me, triggered: true)) return;
        Cast(26058, Me, triggered: true);
        Cast(26284, Me, triggered: true);
        _submerged = true;
        _submergeMs = 30_000;
        _invisMs = 2000;
        _graceMs = _noMeleeMs = 10_000;
        Me.UnitFlags |= HiddenFlags;
        Me.Map?.Combat.AttackStop(Me);
        ResetThreat();
        SetMeleeEnabled(false);
    }

    private void Emerge()
    {
        Me.UnitFlags &= ~HiddenFlags;
        if (System is { } system && system.FindCreature(_trigger) is { } trigger)
        {
            system.NearTeleport(Me, trigger.X, trigger.Y, trigger.Z, Me.Orientation);
            system.ForcedDespawn(trigger, 0);
        }
        Cast(26262, Me, triggered: true);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 26063);
        _submerged = false;
        _summonBase = true;
        _submergeMs = 90_000;
        _sweepMs = 20_500;
        _sandblastMs = RandomDelay(20_000, 25_000);
        _graceMs = _noMeleeMs = 10_000;
        SetMeleeEnabled(true);
        System?.SetInCombatWithZone(Me);
        if (RandomTarget() is { } newVictim) AttackStart(newVictim);
        foreach (Unit nearby in Me.Combat.Threat.Entries.Select(e => e.Target).ToArray())
            if (nearby.IsAlive && (nearby.X - Me.X) * (nearby.X - Me.X) + (nearby.Y - Me.Y) * (nearby.Y - Me.Y) < 20f * 20f)
                Cast(26100, nearby, triggered: true);
        CleanupAdds(includeScarabs: false);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_submerged)
        {
            Due(ref _invisMs, diffMs);
            if (Due(ref _submergeMs, diffMs)) Emerge();
            return;
        }
        if (!UpdateVictim()) return;
        if (_summonBase)
        {
            Cast(26133, Me, triggered: true);
            Cast(26594, Me, triggered: true);
            _summonBase = false;
        }

        if (Due(ref _sweepMs, diffMs) && Cast(26103, Me)) _sweepMs = 20_500;
        if (Due(ref _sandblastMs, diffMs) && Victim is Player victim && Cast(26102, victim))
            _sandblastMs = RandomDelay(20_000, 25_000);

        if (!_enraged && HealthBelowPct(20) && Cast(26615, Me, triggered: true)) _enraged = true;
        if (_enraged)
        {
            if (Due(ref _moundMs, diffMs) && Cast(26617, Me, triggered: true)) _moundMs = 10_000;
        }
        else if (Due(ref _submergeMs, diffMs)) Submerge();

        if (Victim is { } current && MapCombat.CanReachWithMeleeAutoAttack(Me, current))
            _noMeleeMs = Math.Max(3000u, _graceMs);
        else if (_enraged)
        {
            if (RandomTarget() is { } target) Cast(26616, target);
        }
        else if (Due(ref _noMeleeMs, diffMs)) Submerge();
        _graceMs = _graceMs > diffMs ? _graceMs - diffMs : 0;
    }
}

/// <summary>Dirt mounds follow a player and split into scarabs after thirty seconds.</summary>
public sealed class OuroMoundAI(Creature creature) : CreatureAI(creature)
{
    private uint _despawnMs = 30_000;
    private uint _retargetMs;

    public override void OnRespawn()
    {
        _despawnMs = 30_000;
        _retargetMs = 0;
        Me.UnitFlags |= UnitFlags.NotSelectable | UnitFlags.Spawning;
        DoCast(Me, 26092, triggered: true);
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_retargetMs <= diffMs && Me.Map is { } map)
        {
            Player[] targets = [.. map.Players.Where(p => p.IsAlive && !p.IsGameMaster)];
            if (targets.Length > 0) Me.Motion.MoveFollow(targets[System?.RandomInt(0, targets.Length - 1) ?? 0], 0, 0);
            _retargetMs = (uint)(System?.RandomInt(0, 10_000) ?? 5000);
        }
        else _retargetMs -= diffMs;

        if (Due(ref _despawnMs, diffMs))
        {
            DoCast(Me, 26060, triggered: true);
            System?.ForcedDespawn(Me, 0);
        }
    }

    private static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }
}

/// <summary>Ouro scarabs may engage a nearby player and despawn after forty-five seconds.</summary>
public sealed class OuroScarabAI(Creature creature) : CreatureAI(creature)
{
    private uint _despawnMs = 45_000;
    public override void OnRespawn() => _despawnMs = 45_000;
    public override void MoveInLineOfSight(Unit who)
    {
        if (Victim is null && who is Player { IsGameMaster: false, IsAlive: true } player
            && System?.RandomInt(0, 5) == 0) AttackStart(player);
    }
    public override void OnUpdate(uint diffMs)
    {
        _despawnMs = _despawnMs > diffMs ? _despawnMs - diffMs : 0;
        if (_despawnMs == 0) System?.ForcedDespawn(Me, 0);
    }
}
