using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Instances.Scripts.Raids;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Instances.Scripts.ZulGurub;

/// <summary>
/// mangos-classic eastern_kingdoms/zulgurub/zulgurubScripts.cpp npc_soulflayerAI (11359). JustRespawned picks Fear or Knockdown and one of
/// Enrage/Frenzy/Thrash (Thrash is a passive put on at once). Reset: the CC timer rolls 2-5 s, Soul Tap from it to 7 s, Lightning Breath
/// from that to 9 s. Soul Tap (random target) and Lightning Breath repeat every 10-15 s, the CC every 8-10 s; the timers pause while it
/// casts. At or below 30% it casts Enrage or Frenzy whenever the aura is missing.
/// </summary>
public sealed class SoulflayerAI : RaidBossAI
{
    public const uint Entry = 11359;
    public const uint SoulTap = 24619, LightningBreath = 20543, Thrash = 8876, Knockdown = 20276, Fear = 22678, Frenzy = 28371, Enrage = 8269;

    private uint _soulTapMs, _breathMs, _ccMs;

    public SoulflayerAI(Creature creature) : base(creature, null)
    {
        CcSpell = Fear;
        BuffSpell = Enrage;
    }

    public uint CcSpell { get; private set; }
    public uint BuffSpell { get; private set; }

    public override void OnRespawn()
    {
        CcSpell = (System?.RandomInt(0, 1) ?? 0) == 0 ? Fear : Knockdown;
        BuffSpell = (System?.RandomInt(0, 2) ?? 0) switch { 0 => Enrage, 1 => Frenzy, _ => Thrash };
        base.OnRespawn();
        if (BuffSpell == Thrash && !(System?.HasAura(Me, Thrash) ?? false)) Cast(Thrash, Me, triggered: true);
    }

    protected override void ResetActions()
    {
        base.ResetActions();
        _ccMs = RandomDelay(2000, 5000);
        _soulTapMs = RandomDelay((int)_ccMs, 7000);
        _breathMs = RandomDelay((int)_soulTapMs, 9000);
        if (BuffSpell == Thrash && !(System?.HasAura(Me, Thrash) ?? false)) Cast(Thrash, Me, triggered: true);
    }

    public (uint SoulTap, uint Breath, uint Cc) Timers => (_soulTapMs, _breathMs, _ccMs);

    private static bool Due(ref uint timer, uint diffMs)
    {
        if (timer < diffMs) return true;
        timer -= diffMs;
        return false;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (System?.AiServices.Spells?.IsCasting(Me) == true) return;
        if (Due(ref _soulTapMs, diffMs) && RandomTarget() is { } target && Cast(SoulTap, target)) _soulTapMs = RandomDelay(10000, 15000);
        if (Due(ref _breathMs, diffMs) && Cast(LightningBreath, Victim)) _breathMs = RandomDelay(10000, 15000);
        if (Due(ref _ccMs, diffMs) && Cast(CcSpell, Victim)) _ccMs = RandomDelay(8000, 10000);
        if (BuffSpell != Thrash && Below(30) && !(System?.HasAura(Me, BuffSpell) ?? false)) Cast(BuffSpell, Victim);
    }
}

/// <summary>
/// mangos-classic eastern_kingdoms/zulgurub/boss_jeklik.cpp npc_gurubashi_bat_riderAI (14750). A rider Jeklik summoned is passive and does
/// nothing on aggro; a placed rider casts Demoralizing Shout on aggro and loses UNIT_FLAG_UNINTERACTIBLE. Reset keeps Thrash on. Once below
/// 40% it casts Unstable Concoction once and emotes SAY_SELF_DETONATE (broadcast_text 10425).
/// </summary>
public sealed class GurubashiBatRiderAI : RaidBossAI
{
    public const uint Entry = 14750;
    public const uint Thrash = 8876, DemoralizingShout = 23511, UnstableConcoction = 24024;

    public GurubashiBatRiderAI(Creature creature) : base(creature, null)
    {
        IsSummon = creature.Spawn is null;
        if (IsSummon) creature.ReactState = CreatureReactState.Passive;
    }

    public bool IsSummon { get; }
    public bool HasDoneConcoction { get; private set; }

    protected override void ResetActions()
    {
        base.ResetActions();
        HasDoneConcoction = false;
        if (!(System?.HasAura(Me, Thrash) ?? false)) Cast(Thrash, Me, triggered: true);
    }

    public override void OnAggro(Unit target)
    {
        base.OnAggro(target);
        if (IsSummon) return;
        Cast(DemoralizingShout, Me);
        Me.UnitFlags &= ~UnitFlags.NotSelectable;
    }

    protected override void UpdateCombat(uint diffMs)
    {
        if (!HasDoneConcoction && HealthBelowPct(40) && Cast(UnstableConcoction))
        {
            System?.SayText(Me, 10425);
            HasDoneConcoction = true;
        }
    }
}

/// <summary>
/// mangos-classic zulgurubScripts.cpp WyvernStingAura (24335): when the sting expires (not when it is dispelled), its caster casts
/// Wyvern Sting Dot (24336) at the target.
/// </summary>
public sealed class WyvernStingAuraModule : ISpellHandlerModule
{
    public const uint WyvernSting = 24335, WyvernStingDot = 24336;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.HolderRemoved += holder =>
        {
            if (holder.Spell.Id != WyvernSting || holder.RemoveMode != AuraRemoveMode.Expire || !holder.Target.IsAlive) return;
            if (holder.Target.Map?.FindObject(holder.CasterGuid) is Unit { IsAlive: true } caster)
                system.CastSpell(caster, WyvernStingDot, SpellCastTargets.ForUnit(holder.Target.Guid), triggered: true);
        };
    }
}

