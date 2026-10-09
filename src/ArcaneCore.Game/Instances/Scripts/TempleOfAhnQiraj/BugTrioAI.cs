using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// vmangos scripts/kalimdor/silithus/temple_of_ahnqiraj/boss_bug_trio.cpp:
/// boss_bug_trioAI Aggro, JustDied, TriggerDevour, UpdateAI, LeashEncounter;
/// boss_kriAI, boss_yaujAI and boss_vemAI Reset, UpdateBugAI, JustDied, SpellHitTarget.
/// GPL reference supplies timings and behavior only.
/// </summary>
public abstract class BugTrioAI(Creature creature, TempleOfAhnQirajInstance temple)
    : RaidBossAI(creature, TempleOfAhnQirajInstance.BugTrio)
{
    private bool _eating;
    private uint _devour;
    private uint _leash = 2500;
    protected TempleOfAhnQirajInstance Temple { get; } = temple;
    public bool IsEating => _eating;

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        foreach (Creature other in System?.Creatures.Where(c => c.IsAlive && c.Guid != Me.Guid && c.Entry is 15511 or 15543 or 15544) ?? [])
            if (!other.Combat.IsInCombat) other.AI?.AttackStart(target);
    }

    public override void OnDeath(Unit? killer)
    {
        Temple.BugDied(Me);
        if (Temple.GetData(TempleOfAhnQirajInstance.BugTrio) == EncounterState.Done) return;
        Me.ClearLootRecipient();
        System?.ForcedDespawn(Me, 4000);
        foreach (Creature survivor in System?.Creatures.Where(c => c.IsAlive && c.Guid != Me.Guid && c.Entry is 15511 or 15543 or 15544) ?? [])
            if (survivor.AI is BugTrioAI ai) ai.BeginDevour(Me);
    }

    private void BeginDevour(Creature corpse)
    {
        _eating = true;
        _devour = 4000;
        Me.Motion.MovePoint(1, corpse.X, corpse.Y, corpse.Z, run: true);
    }

    public override void OnEvade()
    {
        _eating = false;
        _leash = 2500;
        base.OnEvade();
    }

    public override void OnReachedHome()
    {
        Temple.SetData(TempleOfAhnQirajInstance.BugTrio, EncounterState.Fail);
        if (System is { } system) Temple.RestoreBugTrio(system);
        foreach (Creature other in System?.Creatures.Where(c => c.Entry is 15511 or 15543 or 15544) ?? [])
            if (other.Guid != Me.Guid && other.Combat.IsInCombat) other.AI?.EnterEvadeMode();
    }

    public override void OnUpdate(uint diffMs)
    {
        if (_eating)
        {
            if (_devour > diffMs) { _devour -= diffMs; return; }
            _devour = 0;
            _eating = false;
            Cast(17683, triggered: true);
            Me.Health = Me.MaxHealth;
            foreach (var threat in Me.Combat.Threat.Entries.ToArray())
                Me.Combat.Threat.ModifyThreatPercent(threat.Target, -100);
            if (Victim is { } victim) Me.Motion.MoveChase(victim);
        }
        if (!Me.Combat.IsInCombat || !UpdateVictim()) return;
        if (_leash <= diffMs)
        {
            _leash = 2500;
            if (Me.Y < 2060 && Me.X > -8600) { EnterEvadeMode(); return; }
        }
        else _leash -= diffMs;
        UpdateCombat(diffMs);
    }

    protected Unit? RandomTarget(Func<Unit, bool> predicate)
    {
        Unit[] targets = Me.Combat.Threat.Entries.Select(e => e.Target).Where(u => u.IsAlive && predicate(u)).ToArray();
        return targets.Length == 0 ? null : targets[System?.RandomInt(0, targets.Length - 1) ?? 0];
    }
}

public sealed class KriAI : BugTrioAI
{
    public KriAI(Creature creature, TempleOfAhnQirajInstance temple) : base(creature, temple)
    {
        AddAction(4000, 8000, () => Cast(26350, Victim), () => RandomDelay(5000, 12000));
        AddAction(8000, 10000, () => Cast(25812), () => RandomDelay(8000, 14000));
        AddAction(4000, 7000, () => Cast(3391), () => RandomDelay(2000, 8000));
    }
    public override void OnDeath(Unit? killer) { Cast(26590, triggered: true); base.OnDeath(killer); }
}

public sealed class YaujAI : BugTrioAI
{
    public YaujAI(Creature creature, TempleOfAhnQirajInstance temple) : base(creature, temple)
    {
        // SD2's encounter spell avoids vmangos' temporary Magmadar panic workaround.
        AddAction(10000, 20000, () => { if (!Cast(26580)) return false; ClearThreat(); return true; }, () => 20000);
        AddAction(10000, 20000, () =>
        {
            Unit? target = (ulong)Me.Health * 100 <= (ulong)Me.MaxHealth * 93 ? Me :
                System?.Creatures.Where(c => c.IsAlive && c.FactionTemplate == Me.FactionTemplate &&
                    DistanceSquared(c, Me) <= 10000 && c.Health < c.MaxHealth)
                    .OrderByDescending(c => c.MaxHealth - c.Health).FirstOrDefault();
            return target is not null && Cast(25807, target);
        }, () => 12000);
        AddAction(4000, 9000, () => Cast(3242, Victim), () => RandomDelay(12000, 20000));
    }

    private static float DistanceSquared(Unit a, Unit b)
        => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    private void ClearThreat()
    {
        foreach (var threat in Me.Combat.Threat.Entries.ToArray())
            Me.Combat.Threat.ModifyThreatPercent(threat.Target, -100);
    }

    public override void OnDeath(Unit? killer)
    {
        for (int i = 0; i < 10; ++i)
        {
            // vmangos boss_yaujAI::JustDied rolls a point within 40 yards and rejects a point
            // outside line of sight from the room center. Bound retries if collision data has no valid point.
            for (int attempt = 0; attempt < 20; ++attempt)
            {
                float angle = (System?.RandomInt(0, 6283) ?? 0) / 1000f;
                float radius = System?.RandomInt(0, 40) ?? 0;
                float x = Me.X + radius * MathF.Cos(angle), y = Me.Y + radius * MathF.Sin(angle);
                float floor = Me.Map?.Collision.GetHeight(x, y, Me.Z) ?? TerrainTile.InvalidHeightValue;
                float z = floor > TerrainTile.InvalidHeight ? floor : Me.Z;
                if (Me.Map?.Collision.IsInLineOfSight(-8590, 2138, 0, x, y, z) == false) continue;
                if (System?.SummonAt(Me, 15621, x, y, z, 0, null, 10000) is { } brood)
                    System.SetInCombatWithZone(brood);
                break;
            }
        }
        base.OnDeath(killer);
    }
}

public sealed class VemAI : BugTrioAI
{
    public VemAI(Creature creature, TempleOfAhnQirajInstance temple) : base(creature, temple)
    {
        AddAction(10000, 15000, () =>
        {
            Unit? target = RandomTarget(u => !MapCombat.CanReachWithMeleeAutoAttack(Me, u));
            return target is not null && Cast(26561, target);
        }, () => RandomDelay(15000, 20000));
        AddAction(15000, 20000, () => Victim is { } victim && MapCombat.CanReachWithMeleeAutoAttack(Me, victim)
            && Cast(18670, victim), () => RandomDelay(10000, 14000));
        // vmangos boss_vemAI::UpdateBugAI: Knockdown needs someone in melee range, then hits the current victim.
        AddAction(5000, 8000, () => Victim is { } victim
            && RandomTarget(u => MapCombat.CanReachWithMeleeAutoAttack(Me, u)) is not null
            && Cast(19128, victim), () => RandomDelay(15000, 20000));
    }
    public override void OnDeath(Unit? killer) { Cast(25790, triggered: true); base.OnDeath(killer); }
    public override void OnSpellHitTarget(Unit target, SpellInfo spell)
    {
        if (spell.Id == 18670 && target is Player) Me.Combat.Threat.ModifyThreatPercent(target, -80);
    }
}
