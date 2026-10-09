using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Maps.Collision;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// Fankriss the Unyielding (15510), vmangos boss_fankriss.cpp: three rotating webs,
/// hatchlings in every alcove, staged Spawn of Fankriss waves and Mortal Wound.
/// </summary>
public sealed class FankrissAI : RaidBossAI
{
    private static readonly (float X, float Y, float Z)[] WormSites =
    [(-8076.53f, 1120.37f, -88.50f), (-8150.18f, 1146.97f, -87.45f), (-8023.31f, 1242.42f, -83.47f)];
    private static readonly (uint Spell, float X, float Y, float Z)[] WebSites =
    [(720, -8043.01f, 1254.20f, -84.19f), (731, -8003.00f, 1222.90f, -82.10f),
        (1121, -8022.68f, 1150.08f, -89.33f)];
    private static readonly (float X, float Y, float Z) PullCenter = (-8074.88f, 1193.64f, -92.11f);
    private readonly (uint Timer, bool Fired)[] _webs = new (uint, bool)[3];
    private readonly (uint Timer, uint Enrage, bool Enabled, bool Spawned)[] _worms = new (uint, uint, bool, bool)[3];
    private readonly int[] _webOrder = [0, 1, 2];
    private readonly int[] _wormOrder = [0, 1, 2];
    private readonly HashSet<ObjectGuid> _hatchlings = [];
    private uint _mortalWound;
    private uint _webRotation;
    private uint _leash;
    private uint _lastWormWave = 1;

    public FankrissAI(Creature creature) : base(creature, TempleOfAhnQirajInstance.Fankriss) => ResetActions();

    protected override void ResetActions()
    {
        base.ResetActions();
        _mortalWound = RandomDelay(4000, 8000);
        _leash = 2500;
        _lastWormWave = 1;
        Array.Clear(_worms);
        _worms[0] = (RandomDelay(20_000, 30_000), 15_000, true, false);
        _hatchlings.Clear();
        Shuffle(_wormOrder);
        ResetWebs(8000);
    }

    private void Shuffle(int[] order)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = System?.RandomInt(0, i) ?? 0;
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    private void ResetWebs(uint addedMs)
    {
        Shuffle(_webOrder);
        _webs[0] = (RandomDelay(2000, 18_000) + addedMs, false);
        _webs[1] = (RandomDelay(15_000, 28_000) + addedMs, false);
        _webs[2] = (RandomDelay(25_000, 45_000) + addedMs, false);
        _webRotation = 45_000 + addedMs;
    }

    private static bool Due(ref uint timer, uint diffMs)
    {
        timer = timer > diffMs ? timer - diffMs : 0;
        return timer == 0;
    }

    private Player? RandomPlayer()
    {
        Player[] candidates = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && ReferenceEquals(p.Map, Me.Map))];
        return candidates.Length == 0 ? null : candidates[System?.RandomInt(0, candidates.Length - 1) ?? 0];
    }

    public override void MoveInLineOfSight(Unit who)
    {
        if (who is Player && Victim is null && System is { } system && system.CanAggroOnSight(Me, who, scriptedRange: 100f))
            system.EnterCombatWithTarget(Me, who);
        base.MoveInLineOfSight(who);
    }

    public override void OnUpdate(uint diffMs)
    {
        // vmangos checks the room's pull point even without a relocation notification.
        if (Victim is null && Me.Map is { } map && System is { } system)
        {
            foreach (Player player in map.Players)
            {
                float dx = player.X - PullCenter.X, dy = player.Y - PullCenter.Y, dz = player.Z - PullCenter.Z;
                if (player.IsAlive && !player.IsGameMaster && player.Z <= -70f
                    && (dx * dx) + (dy * dy) + (dz * dz) < 80f * 80f
                    && map.Collision.IsWithinLineOfSight(player, Me)
                    && system.CanAggroOnSight(Me, player, scriptedRange: 100f))
                {
                    system.EnterCombatWithTarget(Me, player);
                    break;
                }
            }
        }
        base.OnUpdate(diffMs);
    }

    public override void OnJustSummoned(Creature summoned)
    {
        if (summoned.Entry == 15962) _hatchlings.Add(summoned.Guid);
        else if (summoned.Entry == 15630)
        {
            System?.SetInCombatWithZone(summoned);
            if (RandomTarget() is { } target) summoned.AI?.AttackStart(target);
        }
    }

    public override void OnSummonedCreatureJustDied(Creature summoned) => _hatchlings.Remove(summoned.Guid);
    public override void OnSummonedCreatureDespawn(Creature summoned) => _hatchlings.Remove(summoned.Guid);

    private void SpawnHatchlings()
    {
        if (System is not { } system) return;
        foreach (var site in WebSites)
        {
            int capacity = Math.Min(4, 20 - _hatchlings.Count);
            int count = capacity > 2 ? (int)RandomDelay(2, capacity) : capacity;
            for (int i = 0; i < count; i++)
                system.SummonAt(Me, 15962, site.X, site.Y, site.Z, 0, null, 65_000);
        }
    }

    private void UpdateWebs(uint diffMs)
    {
        for (int i = 0; i < _webs.Length; i++)
        {
            if (_webs[i].Fired) continue;
            uint timer = _webs[i].Timer;
            if (Due(ref timer, diffMs) && RandomPlayer() is { } target && Cast(WebSites[_webOrder[i]].Spell, target))
            {
                _webs[i] = (0, true);
                SpawnHatchlings();
            }
            else _webs[i].Timer = timer;
        }

        if (Due(ref _webRotation, diffMs) && _webs.All(w => w.Fired)) ResetWebs(0);
    }

    private void SpawnWorm(int slot)
    {
        if (System is not { } system) return;
        var site = WormSites[_wormOrder[slot]];
        if (system.SummonAt(Me, 15630, site.X, site.Y, site.Z, 0, null, 50_000)?.AI is SpawnOfFankrissAI worm)
            worm.SetEnrageDelay(_worms[slot].Enrage);
    }

    private void UpdateWorms(uint diffMs)
    {
        bool allSpawned = true;
        for (int i = 0; i < _worms.Length; i++)
        {
            if (!_worms[i].Enabled || _worms[i].Spawned) continue;
            uint timer = _worms[i].Timer;
            if (Due(ref timer, diffMs))
            {
                _worms[i].Spawned = true;
                SpawnWorm(i);
            }
            else
            {
                _worms[i].Timer = timer;
                allSpawned = false;
            }
        }
        if (!allSpawned) return;

        Shuffle(_wormOrder);
        uint count = RandomDelay(1, 3);
        uint first = 18_000 + ((_lastWormWave - 1) * 7000) + RandomDelay(0, 5000);
        for (int i = 0; i < _worms.Length; i++)
        {
            bool enabled = i < count;
            uint timer = i == 0 ? first : _worms[i - 1].Timer + RandomDelay(4000, 8000);
            _worms[i] = (timer, 15_000 + (uint)i * 5000, enabled, false);
        }
        _lastWormWave = count;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (Due(ref _mortalWound, diffMs) && Cast(25646, Victim)) _mortalWound = RandomDelay(4000, 8000);
        UpdateWorms(diffMs);
        UpdateWebs(diffMs);
        if (Due(ref _leash, diffMs))
        {
            _leash = 2500;
            if (Me.Y > 1400) EnterEvadeMode();
        }
    }
}

/// <summary>Spawn of Fankriss (15630): the 1.10.0 rule enrages it after its wave delay.</summary>
public sealed class SpawnOfFankrissAI(Creature creature) : RaidBossAI(creature, null)
{
    private uint _enrage = 10_000;
    private bool _enraged;
    public void SetEnrageDelay(uint delayMs) => _enrage = delayMs;
    protected override void ResetActions() { base.ResetActions(); _enrage = 10_000; _enraged = false; }
    public override void OnRespawn() => ResetActions();
    protected override void UpdateCombat(uint diffMs)
    {
        if (_enraged) return;
        _enrage = _enrage > diffMs ? _enrage - diffMs : 0;
        if (_enrage == 0 && Cast(26662, Me, triggered: true)) _enraged = true;
    }
}

/// <summary>Vekniss Hatchling (15962): waits 2.5 seconds before engaging the nearest target.</summary>
public sealed class FankrissHatchlingAI(Creature creature) : AggressorAI(creature)
{
    private uint _engage = 2500;
    private bool _ready;

    public override bool AttackStart(Unit target) => _ready && base.AttackStart(target);
    public override void MoveInLineOfSight(Unit who) { if (_ready) base.MoveInLineOfSight(who); }
    public override void OnAttackedBy(Unit attacker)
    {
        _ready = true;
        _engage = 0;
        base.OnAttackedBy(attacker);
    }
    public override void OnRespawn() { _engage = 2500; _ready = false; }
    public override void OnUpdate(uint diffMs)
    {
        if (!_ready)
        {
            _engage = _engage > diffMs ? _engage - diffMs : 0;
            if (_engage != 0) return;
            _ready = true;
            System?.SetInCombatWithZone(Me);
            Unit? nearest = Me.Combat.Threat.Entries.Select(e => e.Target)
                .Where(u => u.IsAlive && u.IsInWorld && ReferenceEquals(u.Map, Me.Map))
                .OrderBy(u => ((u.X - Me.X) * (u.X - Me.X)) + ((u.Y - Me.Y) * (u.Y - Me.Y))).FirstOrDefault();
            if (nearest is { } target)
            {
                float dx = target.X - Me.X, dy = target.Y - Me.Y;
                if ((dx * dx) + (dy * dy) <= 200f * 200f) AttackStart(target);
            }
        }
        base.OnUpdate(diffMs);
    }
}
