using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.Naxxramas;

/// <summary>
/// mangos-classic naxxramas/boss_sapphiron.cpp boss_sapphironAI::{Aggro,ExecuteAction,
/// HandleLandingPhase,HandleGroundPhase}, IceBolt/PeriodicIceBolt. Five icebolts precede breath;
/// the icebolt immunity aura is the 1.12 protection behind a summoned ice block.
/// </summary>
public sealed class SapphironAI : RaidBossAI
{
    private enum Phase { Ground, Air, Breath, Landing }
    private Phase _phase;
    private uint _phaseTime;
    private uint _airDelay = 46000;
    private int _icebolts;
    private readonly HashSet<ObjectGuid> _iceTargets = [];

    public SapphironAI(Creature creature) : base(creature, NaxxramasInstance.Sapphiron)
    {
        AddAction(5000, () => _phase != Phase.Ground || Cast(19983, Victim), () => RandomDelay(5000, 10000));
        AddAction(12000, () => _phase != Phase.Ground || Cast(15847, Me), () => RandomDelay(7000, 10000));
        AddAction(12000, () => _phase != Phase.Ground || Cast(28542, Me), () => 24000);
        AddAction(10000, () => _phase != Phase.Ground || Cast(28560, Me), () => 20000);
        AddAction(900000, () => Cast(26662, Me), () => 300000);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(19818, Me, triggered: true);
        Cast(28529, Me, triggered: true);
        System?.SetInCombatWithZone(Me);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _phase = Phase.Ground;
        _phaseTime = 0;
        _airDelay = 46000;
        _icebolts = 0;
        _iceTargets.Clear();
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (_phase == Phase.Ground)
        {
            if ((ulong)Me.Health * 100 > (ulong)Me.MaxHealth * 10)
            {
                if (_airDelay > diffMs) _airDelay -= diffMs;
                else
                {
                    _phase = Phase.Air;
                    _phaseTime = 6000;
                    _icebolts = 0;
                    _iceTargets.Clear();
                    Cast(18430, Me, triggered: true);
                    MeleeEnabled = false;
                }
            }
            base.UpdateCombat(diffMs);
            return;
        }

        if (_phaseTime > diffMs) { _phaseTime -= diffMs; return; }
        switch (_phase)
        {
            case Phase.Air:
                if (_icebolts < 5)
                {
                    Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
                        .Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map && !_iceTargets.Contains(p.Guid))];
                    if (targets.Length > 0)
                    {
                        Unit target = targets[System!.RandomInt(0, targets.Length - 1)];
                        if (Cast(28522, target))
                        {
                            Cast(31800, target, triggered: true);
                            Cast(28535, target, triggered: true);
                            _iceTargets.Add(target.Guid);
                        }
                    }
                    _icebolts++;
                    _phaseTime = 3500;
                    break;
                }
                Cast(30101, Me, triggered: true);
                _phase = Phase.Breath;
                _phaseTime = 500;
                break;
            case Phase.Breath:
                if (!Cast(28524, Me)) { _phaseTime = 100; break; }
                _phase = Phase.Landing;
                _phaseTime = 10000;
                break;
            case Phase.Landing:
                System?.RemoveAuras(Me, 18430);
                _phase = Phase.Ground;
                _airDelay = 46000;
                _phaseTime = 0;
                _iceTargets.Clear();
                MeleeEnabled = true;
                break;
        }
    }
}

/// <summary>
/// mangos-classic naxxramas/boss_kelthuzad.cpp boss_kelthuzadAI::{SpellHit,
/// UpdateSummoning,StartPhase2,ExecuteAction,HandleLichKingAnswer}: five-minute
/// channel and add timeline, guardians below 40%. mangos-classic drives phase two from a
/// creature spell list; the timers here are vmangos boss_kelthuzad.cpp UpdateP2P3/DoChains
/// (first casts at 10/14/20/30/50/60 s, with its fissure/blast/volley spacing).
/// </summary>
public sealed class KelThuzadAI : RaidBossAI
{
    private enum Phase { Adds, Combat, Guardians }
    private Phase _phase;
    private uint _elapsed;
    private uint _frostBolt, _nova, _chains, _mana, _fissure, _blast, _guardianDelay;
    private uint _sinceBlast, _sinceFissure, _sinceNova;
    private uint _guardianAuraMs;
    private bool _guardianStarted;
    private bool _channelStarted;
    private static readonly (uint Second, uint Spell)[] SoldierTimeline =
        [(7, 29410), (73, 29391), (134, 28425), (189, 29392), (244, 29409)];
    private static readonly (uint Second, uint Spell)[] AbominationTimeline =
        [(15, 28426), (76, 29393), (127, 29394), (228, 29398), (259, 29411)];
    private static readonly (uint Second, uint Spell)[] WeaverTimeline =
        [(1, 29399), (42, 29401), (63, 28427), (154, 29412)];

    public KelThuzadAI(Creature creature) : base(creature, NaxxramasInstance.KelThuzad) { }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Me.UnitFlags |= UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable;
        MeleeEnabled = false;
        _channelStarted = Cast(29423, Me, triggered: true);
        System?.SetInCombatWithZone(Me);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _phase = Phase.Adds;
        _elapsed = 0;
        _channelStarted = false;
        // vmangos boss_kelthuzad.cpp EVENT_PHASE_TWO_START schedule.
        _frostBolt = 10000; _fissure = 14000; _mana = 20000;
        _nova = 30000; _blast = 50000; _chains = 60000; _guardianDelay = 4000;
        _sinceBlast = _sinceFissure = _sinceNova = 0;
        _guardianAuraMs = 0;
        _guardianStarted = false;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (_phase == Phase.Adds)
        {
            uint before = _elapsed / 1000;
            _elapsed = (uint)Math.Min(uint.MaxValue, (ulong)_elapsed + diffMs);
            uint after = _elapsed / 1000;
            if (!_channelStarted) _channelStarted = Cast(29423, Me, triggered: true);
            if (before < 4 && after >= 4)
                for (int i = 0; i < 9; i++) Cast(28421, Me, triggered: true);
            if (before < 9 && after >= 9)
                for (int i = 0; i < 3; i++) Cast(28422, Me, triggered: true);
            if (before < 12 && after >= 12) Cast(28423, Me, triggered: true);
            CastTimeline(SoldierTimeline, before, after);
            CastTimeline(AbominationTimeline, before, after);
            CastTimeline(WeaverTimeline, before, after);
            if (before < 310 && after >= 310 && System is { } creatures)
                foreach (Creature add in creatures.Creatures.Where(c => c.Entry is (16427 or 16428 or 16429)
                    && !c.Combat.IsInCombat).ToArray())
                    creatures.ForcedDespawn(add, 0);
            if (_elapsed >= 325000)
            {
                _phase = Phase.Combat;
                System?.RemoveAuras(Me, 29423);
                foreach (var timeline in new[] { SoldierTimeline, AbominationTimeline, WeaverTimeline })
                    foreach (var step in timeline) System?.RemoveAuras(Me, step.Spell);
                Me.UnitFlags &= ~(UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable);
                MeleeEnabled = true;
            }
            return;
        }

        if (_phase == Phase.Combat && (ulong)Me.Health * 100 < (ulong)Me.MaxHealth * 40)
        {
            _phase = Phase.Guardians;
            _guardianDelay = 4000;
            (Instance as NaxxramasInstance)?.OpenGuardianWindows();
        }
        if (_phase == Phase.Guardians)
        {
            if (!_guardianStarted)
            {
                if (_guardianDelay > diffMs) _guardianDelay -= diffMs;
                else if (Cast(29898, Me, triggered: true) && Cast(28453, Me, triggered: true))
                {
                    _guardianStarted = true;
                    _guardianAuraMs = 55000;
                    (Instance as NaxxramasInstance)?.StartGuardianChecks();
                }
            }
            else if (_guardianAuraMs > 0)
            {
                if (_guardianAuraMs > diffMs) _guardianAuraMs -= diffMs;
                else
                {
                    _guardianAuraMs = 0;
                    System?.RemoveAuras(Me, 28453);
                }
            }
        }

        UpdatePhaseTwoSpells(diffMs);
    }

    /// <summary>
    /// vmangos boss_kelthuzad.cpp UpdateP2P3: Frost Blast, Shadow Fissure and the Frostbolt volley keep
    /// 5-8 seconds apart; Fissure and Frost Blast pick a random target other than the top of the threat list.
    /// </summary>
    private void UpdatePhaseTwoSpells(uint diffMs)
    {
        _sinceBlast = Saturating(_sinceBlast, diffMs);
        _sinceFissure = Saturating(_sinceFissure, diffMs);
        _sinceNova = Saturating(_sinceNova, diffMs);
        bool casting = System?.AiServices.Spells?.IsCasting(Me) ?? false;

        if (Due(ref _frostBolt, diffMs))
        {
            _frostBolt = RandomDelay(5000, 7000); // vmangos: "todo: this is guesswork"
            Cast(28478, Victim);
        }

        if (Due(ref _nova, diffMs))
        {
            if (_sinceBlast < 5000) _nova = 5000 - _sinceBlast;
            else if (_sinceFissure < 5000) _nova = 5000 - _sinceFissure;
            else if (Cast(28479, Me)) { _nova = RandomDelay(15000, 17000); _sinceNova = 0; }
            else _nova = 1000;
        }

        if (Due(ref _blast, diffMs))
        {
            if (_sinceFissure < 5000) _blast = 5000 - _sinceFissure;
            else if (_sinceNova < 8000) _blast = 8000 - _sinceNova;
            else if (casting) _blast = 1000;
            else if (PickNonTank() is { } target && Cast(27808, target)) { _blast = RandomDelay(30000, 60000); _sinceBlast = 0; }
            else _blast = 1000;
        }

        if (Due(ref _fissure, diffMs))
        {
            if (_sinceBlast < 5000) _fissure = 5000 - _sinceBlast;
            else if (_sinceNova < 8000) _fissure = 8000 - _sinceNova;
            else if (casting) _fissure = 2000;
            else if (PickNonTank() is { } target && Cast(27810, target)) { _fissure = RandomDelay(10000, 20000); _sinceFissure = 0; }
            else _fissure = 1000;
        }

        if (Due(ref _mana, diffMs))
        {
            if (casting) _mana = 2000;
            else
            {
                _mana = RandomDelay(20000, 25000);
                if (PickPlayer(p => p.PowerType == PowerType.Mana) is { } target) Cast(27819, target);
            }
        }

        if (Due(ref _chains, diffMs))
        {
            // vmangos DoChains: the threat list is reset after a successful cast.
            if (!Cast(28408, Me)) _chains = 2000;
            else
            {
                foreach (var entry in Me.Combat.Threat.Entries.ToArray()) Me.Combat.Threat.ModifyThreatPercent(entry.Target, -100);
                _chains = RandomDelay(60000, 75000);
            }
        }
    }

    private static uint Saturating(uint value, uint add) => (uint)Math.Min(uint.MaxValue, (ulong)value + add);

    private static bool Due(ref uint remaining, uint diffMs)
    {
        if (remaining > diffMs) { remaining -= diffMs; return false; }
        remaining = 0;
        return true;
    }

    /// <summary>vmangos SelectAttackingTarget(ATTACKING_TARGET_RANDOM, 1): any threat entry but the first.</summary>
    private Unit? PickNonTank()
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Skip(1).Select(e => e.Target)
            .Where(u => u.IsAlive && u.IsInWorld && u.Map == Me.Map)];
        return targets.Length == 0 ? null : targets[System!.RandomInt(0, targets.Length - 1)];
    }

    public override void OnReachedHome()
    {
        Me.UnitFlags &= ~(UnitFlags.ImmuneToPlayer | UnitFlags.NotSelectable);
        MeleeEnabled = true;
        base.OnReachedHome();
    }

    private void CastTimeline((uint Second, uint Spell)[] timeline, uint before, uint after)
    {
        foreach (var step in timeline)
            if (before < step.Second && after >= step.Second)
            {
                foreach (var old in timeline) System?.RemoveAuras(Me, old.Spell);
                Cast(step.Spell, Me, triggered: true);
            }
    }

    private Unit? PickPlayer(Func<Player, bool> predicate)
    {
        Player[] players = [.. Me.Combat.Threat.Entries.Select(e => e.Target).OfType<Player>()
            .Where(p => p.IsAlive && p.IsInWorld && p.Map == Me.Map && predicate(p))];
        return players.Length == 0 ? null : players[System!.RandomInt(0, players.Length - 1)];
    }
}
