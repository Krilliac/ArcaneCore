using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;

namespace ArcaneCore.Game.Instances.Scripts.TempleOfAhnQiraj;

/// <summary>
/// mangos-classic kalimdor/temple_of_ahnqiraj/mob_anubisath_sentinel.cpp npc_anubisath_sentinelAI (15264). The first sentinel to aggro
/// finds every sentinel within 80 yd (four in a group), shuffles the nine abilities, takes the first and hands one each to the others,
/// who join the fight. Below 30% it enrages once (8599, EMOTE_GENERIC_FRENZY broadcast_text 1191). On death its ability goes to each living
/// sentinel of the group with Heal Brethren (SharePowers on 2400). Evading respawns the dead sentinels of the group.
/// Limits: the reference's dead sentinel casts Transfer Power (2400) with its ability as base points and SharePowers casts the ability
/// and 26565 at the buddy; a dead unit cannot cast here, so each buddy casts both on itself (triggered). EMOTE_SHARE_POWERS (-1531047)
/// has no broadcast_text id in the SD2 table and is not said.
/// </summary>
public sealed class AnubisathSentinelAI : RaidBossAI
{
    public const uint Entry = 15264;
    public const uint Enrage = 8599, HealBrethren = 26565;
    public const float GroupRange = 80f;

    public static readonly uint[] Abilities = [812, 2147, 2148, 2834, 9347, 13022, 19595, 21737, 25777];

    private readonly List<ObjectGuid> _group = [];
    private bool _enraged;

    public AnubisathSentinelAI(Creature creature) : base(creature, null) { }

    public uint Ability { get; private set; }
    public IReadOnlyList<ObjectGuid> Group => _group;

    protected override void ResetActions()
    {
        base.ResetActions();
        Ability = 0;
        _enraged = false;
    }

    public override void OnEvade()
    {
        base.OnEvade();
        if (System is not { } system) return;
        foreach (ObjectGuid guid in _group)
            if (guid != Me.Guid && system.FindCreature(guid) is { IsAlive: false } buddy) system.ForceRespawn(buddy);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        if (System is not { } system) return;
        if (_group.Count == 0)
        {
            _group.AddRange(system.CreaturesOfEntryInRange(Me, Me.Entry, GroupRange).Select(c => c.Guid));
            if (!_group.Contains(Me.Guid)) _group.Insert(0, Me.Guid);
        }

        if (Ability != 0) return;
        uint[] shuffled = [.. Abilities];
        for (int i = shuffled.Length - 1; i > 0; i--)
        {
            int j = system.RandomInt(0, i);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        SetAbility(shuffled[0]);
        int next = 1;
        foreach (ObjectGuid guid in _group)
        {
            if (guid == Me.Guid || system.FindCreature(guid) is not { IsAlive: true, AI: AnubisathSentinelAI buddyAi } buddy) continue;
            if (next < shuffled.Length) buddyAi.SetAbility(shuffled[next++]);
            buddyAi.ShareGroup(_group);
            buddy.AI!.AttackStart(target);
        }
    }

    private void ShareGroup(IEnumerable<ObjectGuid> group)
    {
        if (_group.Count != 0) return;
        _group.AddRange(group);
    }

    /// <summary>SetAbility: a sentinel that already has one keeps it.</summary>
    internal void SetAbility(uint ability)
    {
        if (Ability != 0 || !Abilities.Contains(ability)) return;
        Ability = ability;
        Cast(ability, Me, triggered: true);
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        if (Ability == 0 || System is not { } system) return;
        foreach (ObjectGuid guid in _group)
        {
            if (guid == Me.Guid || system.FindCreature(guid) is not { IsAlive: true } buddy) continue;
            system.CastSpellByUnit(buddy, Ability, buddy, triggered: true);
            system.CastSpellByUnit(buddy, HealBrethren, buddy, triggered: true);
        }
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_enraged && HealthBelowPct(30) && Cast(Enrage, Me))
        {
            System?.SayText(Me, 1191);
            _enraged = true;
        }
    }
}

/// <summary>
/// mangos-classic mob_anubisath_sentinel.cpp npc_anubisath_defenderAI (15277). Each aggro puts on one of the two reflects and arms Meteor or
/// Plague (6-10 s, then 8-12 s) and Thunderclap or Shadow Storm (5-8 s, then 6-10 s); the summon starts at 3-5 s and repeats every 12-16 s
/// (Swarmguard or Warrior by coin). Meteor goes at a random player, Plague at a random non-tank. Below 10% each check flips a coin
/// between Enrage (8269 with EMOTE_GENERIC_FRENZY) and Explode (25698), once. Limit: the reference strips every aura on aggro; this
/// strips only the reflects and the enrage it puts on itself.
/// </summary>
public sealed class AnubisathDefenderAI : RaidBossAI
{
    public const uint Entry = 15277;
    public const uint ShadowStorm = 26555, Meteor = 26558, Enrage = 8269, Explode = 25698, Thunderclap = 26554, Plague = 26556;
    public const uint SummonWarrior = 17431, SummonSwarmguard = 17430, ReflectFireArcane = 13022, ReflectShadowFrost = 19595;

    private uint _meteorMs, _plagueMs, _thunderclapMs, _shadowStormMs, _summonMs;
    private bool _hpCheckDone;

    public AnubisathDefenderAI(Creature creature) : base(creature, null) { }

    public (uint Meteor, uint Plague, uint Thunderclap, uint ShadowStorm, uint Summon) Timers
        => (_meteorMs, _plagueMs, _thunderclapMs, _shadowStormMs, _summonMs);

    protected override void ResetActions()
    {
        base.ResetActions();
        _meteorMs = _plagueMs = _thunderclapMs = _shadowStormMs = 0;
        _summonMs = RandomDelay(3000, 5000);
        _hpCheckDone = false;
    }

    private bool Coin() => (System?.RandomInt(0, 1) ?? 0) == 1;

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        ResetActions();
        foreach (uint aura in new[] { ReflectFireArcane, ReflectShadowFrost, Enrage }) System?.RemoveAuras(Me, aura);
        Cast(Coin() ? ReflectFireArcane : ReflectShadowFrost, Me, triggered: true);
        if (Coin()) _meteorMs = RandomDelay(6, 10) * 1000;
        else _plagueMs = RandomDelay(6, 10) * 1000;
        if (Coin()) _thunderclapMs = RandomDelay(5, 8) * 1000;
        else _shadowStormMs = RandomDelay(5, 8) * 1000;
    }

    /// <summary>A disabled action (0) never fires; an armed one fires when its countdown reaches 0.</summary>
    private static bool Due(ref uint timer, uint diffMs)
    {
        if (timer == 0) return false;
        timer = timer > diffMs ? timer - diffMs : 0;
        if (timer != 0) return false;
        timer = 1; // stays armed until the cast succeeds and sets the next delay
        return true;
    }

    private Unit? RandomPlayer(bool skipTank)
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t is Player && t.IsAlive && t.IsInWorld && (!skipTank || !ReferenceEquals(t, Victim)))];
        return targets.Length == 0 ? null : targets[System!.RandomInt(0, targets.Length - 1)];
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!_hpCheckDone && HealthBelowPct(10))
        {
            if (Coin())
            {
                if (Cast(Enrage, Me)) { System?.SayText(Me, 1191); _hpCheckDone = true; }
            }
            else if (Cast(Explode, Me)) _hpCheckDone = true;
        }

        if (Due(ref _meteorMs, diffMs) && RandomPlayer(false) is { } meteorTarget && Cast(Meteor, meteorTarget, triggered: true))
            _meteorMs = RandomDelay(8, 12) * 1000;
        if (Due(ref _plagueMs, diffMs) && RandomPlayer(true) is { } plagueTarget && Cast(Plague, plagueTarget, triggered: true))
            _plagueMs = RandomDelay(8, 12) * 1000;
        if (Due(ref _shadowStormMs, diffMs) && Cast(ShadowStorm, null, triggered: true)) _shadowStormMs = RandomDelay(6, 10) * 1000;
        if (Due(ref _thunderclapMs, diffMs) && Cast(Thunderclap, null, triggered: true)) _thunderclapMs = RandomDelay(6, 10) * 1000;
        if (Due(ref _summonMs, diffMs) && Cast(Coin() ? SummonSwarmguard : SummonWarrior, null, triggered: true))
            _summonMs = RandomDelay(12, 16) * 1000;
    }
}
