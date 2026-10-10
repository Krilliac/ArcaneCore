using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Creatures.Scripts.WorldBosses;

/// <summary>
/// Azuregos (6109, Azshara): mangos-classic ScriptDevAI scripts/kalimdor/boss_azuregos.cpp (vmangos has no core script; its database runs
/// him). Mana Storm on the nearest attacker, Chill, Frost Breath, Arcane Vacuum with "Come, little ones. Face me!" (broadcast 9071),
/// Reflection and Cleave on the cmangos timers. Mark of Frost: his aura on aggro, and a player he kills gets the mark (23182), credited to him. His gossip
/// is off in combat and back on evade.
/// </summary>
public sealed class AzuregosAI : RaidBossAI
{
    public const uint Entry = 6109;
    public const uint SpellArcaneVacuum = 21147;
    public const uint SpellMarkOfFrostPlayer = 23182;
    public const uint SpellMarkOfFrostAura = 23184;
    public const uint SpellManaStorm = 21097;
    public const uint SpellChill = 21098;
    public const uint SpellFrostBreath = 21099;
    public const uint SpellReflect = 22067;
    public const uint SpellCleave = 19983;
    public const int SayTeleport = 9071; // script_texts -1000100 carries broadcast_text 9071

    public AzuregosAI(Creature creature) : base(creature, null)
    {
        AddAction(5000, 17000, () => NearestTarget() is { } t && Cast(SpellManaStorm, t), () => RandomDelay(18000, 35000));
        AddAction(10000, 30000, () => Cast(SpellChill, Me), () => RandomDelay(13000, 25000));
        AddAction(2000, 8000, () => Cast(SpellFrostBreath, Me), () => RandomDelay(10000, 25000));
        AddAction(30000, () => Teleport(), () => RandomDelay(20000, 30000));
        AddAction(15000, 30000, () => Cast(SpellReflect, Me), () => RandomDelay(20000, 35000));
        AddAction(7000, () => Victim is { } v && Cast(SpellCleave, v), () => 7000);
    }

    private bool Teleport()
    {
        if (!Cast(SpellArcaneVacuum, Me)) return false;
        System?.SayText(Me, SayTeleport);
        return true;
    }

    /// <summary>ATTACKING_TARGET_NEAREST_BY: the closest unit on the threat list.</summary>
    private Unit? NearestTarget()
        => Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && ReferenceEquals(t.Map, Me.Map))
            .OrderBy(t => ((t.X - Me.X) * (t.X - Me.X)) + ((t.Y - Me.Y) * (t.Y - Me.Y)) + ((t.Z - Me.Z) * (t.Z - Me.Z)))
            .FirstOrDefault();

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(SpellMarkOfFrostAura, Me);
        Me.NpcFlags &= ~(uint)NpcFlags.Gossip;
    }

    public override void OnEvade()
    {
        Me.NpcFlags |= (uint)NpcFlags.Gossip;
        base.OnEvade();
    }

    /// <summary>
    /// KilledUnit: <c>victim-&gt;CastSpell(victim, SPELL_MARK_OF_FROST_PLAYER, TRIGGERED_OLD_TRIGGERED, nullptr, nullptr, Azuregos)</c>. The
    /// mark is an aura-only spell, so it is put on the dead player with Azuregos as its caster (the original-caster credit), which a cast
    /// by the dead player could not carry here.
    /// </summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (victim is Player) System?.AddAuraFrom(victim, SpellMarkOfFrostPlayer, Me);
    }
}

/// <summary>
/// Lord Kazzak (12397, Blasted Lands): mangos-classic ScriptDevAI scripts/eastern_kingdoms/boss_kazzak.cpp (vmangos has no core script).
/// Shadow Bolt Volley, Cleave, Thunderclap, Void Bolt, Mark of Kazzak on a random mana user and Twisted Reflection on a random player; after
/// 3 minutes he goes berserk (volleys every 1-3 s). Capture Soul on aggro, and his lines (script_texts -1000147..-1000155) on respawn,
/// aggro, berserk, player kills and death. The Mark of Kazzak aura script (spell_mark_of_lord_kazzak: 21058 and the mark gone when the
/// target's mana is empty) runs here, as a check of the marked players every update.
/// </summary>
public sealed class KazzakAI : RaidBossAI
{
    public const uint Entry = 12397;
    public const uint SpellShadowVolley = 21341;
    public const uint SpellBerserk = 21340;
    public const uint SpellCleave = 20691;
    public const uint SpellThunderclap = 26554;
    public const uint SpellVoidBolt = 21066;
    public const uint SpellMarkOfKazzak = 21056;
    public const uint SpellMarkOfKazzakExplode = 21058;
    public const uint SpellCaptureSoul = 21053;
    public const uint SpellTwistedReflection = 21063;
    public const int SayIntro = -1000147;
    public const int SayAggro1 = -1000148;
    public const int SayAggro2 = -1000149;
    public const int SaySupreme1 = -1000150;
    public const int SaySupreme2 = -1000151;
    public const int SayKill1 = -1000152;
    public const int SayKill2 = -1000153;
    public const int SayKill3 = -1000154;
    public const int SayDeath = -1000155;

    private uint _shadowVolleyMs;
    private uint _cleaveMs;
    private uint _thunderclapMs;
    private uint _voidBoltMs;
    private uint _markMs;
    private uint _reflectionMs;
    private uint _supremeMs;
    private readonly HashSet<Unit> _marked = [];

    public bool Berserk { get; private set; }

    public KazzakAI(Creature creature) : base(creature, null) => ResetTimers();

    private void ResetTimers()
    {
        _shadowVolleyMs = RandomDelay(3000, 12000);
        _cleaveMs = 7000;
        _thunderclapMs = RandomDelay(16000, 20000);
        _voidBoltMs = 30000;
        _markMs = 25000;
        _reflectionMs = 33000;
        _supremeMs = 3 * 60 * 1000;
        Berserk = false;
        _marked.Clear();
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        ResetTimers();
    }

    public override void OnRespawn()
    {
        base.OnRespawn();
        System?.SayText(Me, SayIntro);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        Cast(SpellCaptureSoul, Me, triggered: true);
        System?.SayText(Me, System.RandomInt(0, 1) == 0 ? SayAggro1 : SayAggro2);
    }

    /// <summary>KilledUnit: urand(0, 3), the fourth roll says nothing.</summary>
    public override void OnKilledUnit(Unit victim)
    {
        if (victim is not Player || System is not { } system) return;
        switch (system.RandomInt(0, 3))
        {
            case 0: system.SayText(Me, SayKill1); break;
            case 1: system.SayText(Me, SayKill2); break;
            case 2: system.SayText(Me, SayKill3); break;
        }
    }

    public override void OnDeath(Unit? killer)
    {
        base.OnDeath(killer);
        System?.SayText(Me, SayDeath);
    }

    private static bool Tick(ref uint remaining, uint diffMs)
    {
        if (remaining <= diffMs) { remaining = 0; return true; }
        remaining -= diffMs;
        return false;
    }

    private Unit? RandomTargetWhere(Func<Unit, bool> filter)
    {
        Unit[] targets = [.. Me.Combat.Threat.Entries.Select(e => e.Target)
            .Where(t => t.IsAlive && t.IsInWorld && ReferenceEquals(t.Map, Me.Map) && filter(t))];
        return targets.Length == 0 ? null : targets[System!.RandomInt(0, targets.Length - 1)];
    }

    private static bool UsesMana(Unit unit) => unit.PowerType == PowerType.Mana && unit.GetUInt32(UpdateFields.UnitFieldMaxpower1) > 0;

    private static uint Mana(Unit unit) => unit.GetUInt32(UpdateFields.UnitFieldPower1);

    /// <summary>MarkOfLordKazzak::OnPeriodicTickEnd: a marked mana user at 0 mana explodes (21058) and loses the mark.</summary>
    private void CheckMarks()
    {
        if (_marked.Count == 0 || System is not { } system) return;
        foreach (Unit unit in _marked.ToArray())
        {
            if (!unit.IsInWorld || !system.HasAura(unit, SpellMarkOfKazzak))
            {
                _marked.Remove(unit);
            }
            else if (UsesMana(unit) && Mana(unit) == 0)
            {
                system.CastSpellByUnit(unit, SpellMarkOfKazzakExplode, null, triggered: true);
                system.RemoveAuras(unit, SpellMarkOfKazzak);
                _marked.Remove(unit);
            }
        }
    }

    protected override void UpdateCombat(uint diffMs)
    {
        CheckMarks();

        if (_supremeMs != 0 && Tick(ref _supremeMs, diffMs))
        {
            if (Cast(SpellBerserk, Me))
            {
                System?.SayText(Me, System.RandomInt(0, 1) == 0 ? SaySupreme1 : SaySupreme2);
                Berserk = true;
                _shadowVolleyMs = 1000;
            }
            // "a small amount of time will be lost, but this is acceptable": no retry
        }

        if (Tick(ref _shadowVolleyMs, diffMs) && Cast(SpellShadowVolley, Me))
            _shadowVolleyMs = Berserk ? RandomDelay(1000, 3000) : RandomDelay(5000, 30000);

        if (Tick(ref _cleaveMs, diffMs) && Victim is { } cleaveTarget && Cast(SpellCleave, cleaveTarget))
            _cleaveMs = RandomDelay(8000, 12000);

        if (Tick(ref _thunderclapMs, diffMs) && Cast(SpellThunderclap, null))
            _thunderclapMs = RandomDelay(10000, 14000);

        if (Tick(ref _voidBoltMs, diffMs) && Victim is { } boltTarget && Cast(SpellVoidBolt, boltTarget))
            _voidBoltMs = RandomDelay(15000, 28000);

        // SelectAttackingTarget(RANDOM, 0, SPELL_MARK_OF_KAZZAK, SELECT_FLAG_POWER_MANA)
        if (Tick(ref _markMs, diffMs) && RandomTargetWhere(UsesMana) is { } markTarget && Cast(SpellMarkOfKazzak, markTarget))
        {
            _marked.Add(markTarget);
            _markMs = 20000;
        }

        if (Tick(ref _reflectionMs, diffMs) && RandomTargetWhere(static t => t is Player) is { } reflectTarget &&
            Cast(SpellTwistedReflection, reflectTarget))
            _reflectionMs = 15000;

        base.UpdateCombat(diffMs);
    }
}
