using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>The linked Vek'nilash/Vek'lor encounter, including paired teleport and bug mutation.</summary>
public abstract class TwinEmperorAI(Creature creature) : RaidBossAI(creature, TempleOfAhnQirajInstance.Twins)
{
    private uint _enrageMs;
    private uint _bugMs;
    private uint _idleMs;
    private Player? _afterTeleportTarget;
    protected bool TeleportIdle => _idleMs != 0;

    protected TempleOfAhnQirajInstance? Temple => Instance as TempleOfAhnQirajInstance;
    protected Creature? Brother => Temple?.OtherTwin(Me);
    protected abstract uint BugSpell { get; }
    protected abstract uint NextBugDelay();

    protected void ResetTwin()
    {
        _enrageMs = 60 * 60_000;
        _bugMs = NextBugDelay();
        _idleMs = 0;
        _afterTeleportTarget = null;
        SetMeleeEnabled(true);
        SetCombatMovement(true);
    }

    protected static bool Due(ref uint timer, uint diff)
    {
        timer = timer > diff ? timer - diff : 0;
        return timer == 0;
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (who is Player && Victim is null && _idleMs == 0 && MathF.Abs(Me.Z - who.Z) <= 7
            && System is { } system && system.CanAggroOnSight(Me, who, scriptedRange: 50f))
            system.EnterCombatWithTarget(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnAggro(Unit target)
    {
        if (Temple?.GetData(TempleOfAhnQirajInstance.Twins) == EncounterState.InProgress) return;
        base.OnAggro(target);
        System?.SetInCombatWithZone(Me);
        if (Brother is { IsAlive: true, AI: { } ai } brother)
        {
            ai.AttackStart(target);
            System?.SetInCombatWithZone(brother);
        }

        // The defenders around the entrance join the initial pull in the reference script.
        if (System is { } system)
            foreach (Creature defender in system.Creatures.Where(c => c.Entry == 15277 && c.IsAlive
                && (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) <= 800f * 800f))
            {
                system.SetInCombatWithZone(defender);
                defender.AI?.AttackStart(target);
            }
    }

    public override void OnDeath(Unit? killer)
    {
        if (Temple?.GetData(TempleOfAhnQirajInstance.Twins) == EncounterState.Done) return;
        base.OnDeath(killer);
        if (Brother is { IsAlive: true } brother) Me.Map?.Combat.Kill(killer, brother, durabilityLoss: false);
    }

    public override void OnEvade()
    {
        ResetTwin();
        if (Brother is { IsAlive: true, IsEvading: false } brother && System is { } system)
            system.EnterEvadeMode(brother);
        if (System is { } creatures) Temple?.RestoreTwins(creatures);
    }

    public override void OnReachedHome() => Temple?.SetData(TempleOfAhnQirajInstance.Twins, EncounterState.Fail);

    internal void StartTeleport(float x, float y, float z, float orientation)
    {
        _idleMs = 2000;
        _afterTeleportTarget = null;
        Me.Map?.Combat.AttackStop(Me);
        ResetThreat();
        SetMeleeEnabled(false);
        SetCombatMovement(false);
        System?.NearTeleport(Me, x, y, z, orientation);
        Cast(800, Me, triggered: true);
        Cast(26638, Me, triggered: true);
    }

    private void UpdateTeleportIdle(uint diffMs)
    {
        if (_afterTeleportTarget is null && Me.Map is { } map)
        {
            System?.SetInCombatWithZone(Me);
            _afterTeleportTarget = map.Players.Where(p => p.IsAlive && !p.IsGameMaster)
                .OrderBy(p => (p.X - Me.X) * (p.X - Me.X) + (p.Y - Me.Y) * (p.Y - Me.Y))
                .FirstOrDefault();
            if (_afterTeleportTarget is { } nearest) Me.Combat.Threat.AddThreat(nearest, 3000);
        }

        if (!Due(ref _idleMs, diffMs)) return;
        SetMeleeEnabled(true);
        SetCombatMovement(true);
        if (_afterTeleportTarget is { IsAlive: true } target) AttackStart(target);
        _afterTeleportTarget = null;
    }

    private void MutateBug()
    {
        if (System is not { } system || Me.Map?.Combat.SpellMitigation is not { } spells) return;
        Creature[] bugs = [.. system.Creatures.Where(c => c.Entry is 15316 or 15317 && c.IsAlive
            && MathF.Abs(c.Z - Me.Z) < 12f
            && (c.X - Me.X) * (c.X - Me.X) + (c.Y - Me.Y) * (c.Y - Me.Y) <= 20f * 20f
            && !spells.HasAura(c, 802) && !spells.HasAura(c, 804))];
        if (bugs.Length == 0) return;
        Creature bug = bugs[system.RandomInt(0, bugs.Length - 1)];
        if (bug.AI is TwinBugAI ai && ai.Mutate(BugSpell, Me)) _bugMs = NextBugDelay();
    }

    public override void OnUpdate(uint diffMs)
    {
        if (Me.Z > -95f)
        {
            EnterEvadeMode();
            return;
        }

        if (_idleMs != 0) UpdateTeleportIdle(diffMs);
        else if (!UpdateVictim()) return;

        if (Due(ref _enrageMs, diffMs) && Me.Map?.Combat.SpellMitigation?.HasAura(Me, 26662) != true
            && _idleMs == 0 && Cast(26662, Me, triggered: true)) _enrageMs = 5 * 60_000;

        if (_idleMs == 0 && Due(ref _bugMs, diffMs)) MutateBug();
        if (_idleMs == 0) UpdateTwin(diffMs);
    }

    protected abstract void UpdateTwin(uint diffMs);
}

/// <summary>Vek'lor (15276) controls paired teleports, brother healing, Shadow Bolt, Blizzard and Arcane Burst.</summary>
public sealed class VeklorAI : TwinEmperorAI
{
    private uint _teleportMs, _healMs, _shadowMs, _blizzardMs, _burstMs;
    protected override uint BugSpell => 804;
    protected override uint NextBugDelay() => RandomDelay(7000, 10_000);

    public VeklorAI(Creature creature) : base(creature) => ResetTimers();
    private void ResetTimers()
    {
        ResetTwin();
        CasterChaseDistance = 20f;
        _teleportMs = RandomDelay(30_000, 40_000);
        _healMs = _shadowMs = _burstMs = 0;
        _blizzardMs = RandomDelay(15_000, 20_000);
    }
    public override void OnRespawn() => ResetTimers();
    public override void OnEvade() { base.OnEvade(); ResetTimers(); }

    private void TeleportPair()
    {
        if (Brother is not { AI: TwinEmperorAI other } brother || System is null) return;
        (float x, float y, float z, float o) = (Me.X, Me.Y, Me.Z, Me.Orientation);
        StartTeleport(brother.X, brother.Y, brother.Z, o);
        other.StartTeleport(x, y, z, brother.Orientation);
    }

    private void HealBrother(uint diffMs)
    {
        if (!Due(ref _healMs, diffMs) || Brother is not { IsAlive: true } brother || Me.Map?.Combat.SpellMitigation is not { } spells)
            return;
        float dx = brother.X - Me.X, dy = brother.Y - Me.Y;
        if (dx * dx + dy * dy > 60f * 60f) return;
        if (!Cast(7393, brother)) return;
        spells.CastSpell(brother, 7393, SpellCastTargets.ForUnit(Me.Guid), triggered: true);
        _healMs = 1500;
    }

    protected override void UpdateTwin(uint diffMs)
    {
        if (Due(ref _teleportMs, diffMs))
        {
            _teleportMs = RandomDelay(30_000, 40_000);
            TeleportPair();
            if (TeleportIdle) return;
        }
        HealBrother(diffMs);
        if (Victim is null) return;
        if (Due(ref _burstMs, diffMs) && RandomPlayerInRange(10f, skipTopThreat: false) is { } close
            && Cast(568, close)) _burstMs = RandomDelay(5000, 10_000);
        if (Due(ref _blizzardMs, diffMs) && RandomPlayerInRange(45f, skipTopThreat: true) is { } player
            && Cast(26607, player)) _blizzardMs = RandomDelay(15_000, 20_000);
        if (Due(ref _shadowMs, diffMs) && Cast(26006, Victim)) _shadowMs = RandomDelay(1800, 2500);
    }

    private Player? RandomPlayerInRange(float range, bool skipTopThreat)
    {
        Unit? top = skipTopThreat ? Victim : null;
        Player[] candidates = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && ReferenceEquals(p.Map, Me.Map) && !ReferenceEquals(p, top)
                && (p.X - Me.X) * (p.X - Me.X) + (p.Y - Me.Y) * (p.Y - Me.Y) <= range * range)];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }
}

/// <summary>Vek'nilash (15275) uses Double Attack, Unbalancing Strike, Uppercut and bug mutation.</summary>
public sealed class VeknilashAI : TwinEmperorAI
{
    private uint _strikeMs, _uppercutMs;
    protected override uint BugSpell => 802;
    protected override uint NextBugDelay() => RandomDelay(10_000, 15_000);

    public VeknilashAI(Creature creature) : base(creature) => ResetTimers();
    private void ResetTimers()
    {
        ResetTwin();
        _strikeMs = RandomDelay(8000, 18_000);
        _uppercutMs = RandomDelay(14_000, 29_000);
    }
    public override void OnRespawn() => ResetTimers();
    public override void OnEvade() { base.OnEvade(); ResetTimers(); }

    protected override void UpdateTwin(uint diffMs)
    {
        if (Me.Map?.Combat.SpellMitigation?.HasAura(Me, 18943) != true) Cast(18943, Me, triggered: true);
        if (Due(ref _strikeMs, diffMs) && Cast(26613, Victim)) _strikeMs = RandomDelay(8000, 18_000);
        if (Due(ref _uppercutMs, diffMs) && Me.Combat.Threat.Entries.Select(e => e.Target)
            .FirstOrDefault(t => t.IsAlive && MathF.Abs(t.Z - Me.Z) < 7f
                && (t.X - Me.X) * (t.X - Me.X) + (t.Y - Me.Y) * (t.Y - Me.Y) <= 5f * 5f) is { } melee
            && Cast(26007, melee)) _uppercutMs = RandomDelay(14_000, 29_000);
    }
}

/// <summary>Qiraji scarabs and scorpions become hostile only after an emperor mutates them.</summary>
public sealed class TwinBugAI(Creature creature) : CreatureAI(creature)
{
    private uint _pierceMs = 5000, _acidMs = 6000;

    public bool Mutate(uint spell, Creature emperor)
    {
        if (spell is not (802 or 804) || Me.Map?.Combat.SpellMitigation is not { } spells
            || !spells.AddAura(Me, spell, caster: emperor)) return false;
        Me.FactionTemplate = 14;
        if (spell == 802) Me.Health = Me.MaxHealth;
        System?.SetInCombatWithZone(Me);
        if (emperor.Combat.Victim is { } target) AttackStart(target);
        return true;
    }

    public override void OnDeath(Unit? killer)
    {
        Me.FactionTemplate = 7;
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 802);
        Me.Map?.Combat.SpellMitigation?.RemoveAuras(Me, 804);
    }

    public override void OnRespawn()
    {
        Me.FactionTemplate = 7;
        _pierceMs = 5000;
        _acidMs = 6000;
    }

    public override void OnUpdate(uint diffMs)
    {
        if (!UpdateVictim()) return;
        if (_pierceMs <= diffMs && DoCast(Victim, 6016) == CreatureCastResult.Ok) _pierceMs = 7000;
        else _pierceMs = _pierceMs > diffMs ? _pierceMs - diffMs : 0;
        if (_acidMs <= diffMs && DoCast(Victim, 26050) == CreatureCastResult.Ok) _acidMs = 9000;
        else _acidMs = _acidMs > diffMs ? _acidMs - diffMs : 0;
    }
}
