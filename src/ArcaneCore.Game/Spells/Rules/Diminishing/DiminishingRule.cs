using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules.Application;

namespace ArcaneCore.Game.Spells.Rules.Diminishing;

/// <summary>
/// Diminishing returns as an application rule (vmangos Spell::DoSpellHitOnUnit, Spell.cpp:1733-1800, and
/// Unit::ApplyDiminishingToDuration, Unit.cpp:7650-7670): the level is read when the spell hits, an aura
/// spell raises the target's level when the group applies to it, and the holder's duration is scaled.
/// <see cref="Attach"/> installs the rule and keeps the per-unit stack counters in step with the holders.
/// </summary>
public sealed class DiminishingRule : ISpellApplicationRule
{
    private readonly ConditionalWeakTable<Unit, DiminishingTracker> _trackers = new();
    private readonly ConditionalWeakTable<SpellAuraHolder, GroupBox> _holderGroups = new();
    private readonly uint _resetMs;
    private SpellSystem? _system;

    public DiminishingRule(uint resetMs = 15_000) => _resetMs = resetMs;

    /// <summary>Install the rule on <paramref name="system"/> (after the mechanic-resist rule, which narrows the effect mask first).</summary>
    public void Attach(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        _system = system;
        system.ApplicationRules.Add(this);
        system.HolderAdded += holder => OnHolder(holder, applied: true);
        system.HolderRemoved += holder => OnHolder(holder, applied: false);
        system.UnitDied += unit => GetTracker(unit).Clear();
    }

    /// <summary>The tracker of <paramref name="unit"/> (created on first use).</summary>
    public DiminishingTracker GetTracker(Unit unit) => _trackers.GetValue(unit, static _ => new DiminishingTracker());

    public void Begin(SpellApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        SpellSystem system = application.System;
        DiminishingGroup group = DiminishingClassifier.GetGroup(application.Cast.Spell, application.Cast.IsTriggered);
        DiminishingTracker tracker = GetTracker(application.Target);
        DiminishingLevel level = tracker.GetLevel(group, system.NowMs, _resetMs);
        application.SetState(this, new Hit(group, level));

        if (AppliesAura(application.Cast.Spell, application.EffectMask))
        {
            DiminishingType type = DiminishingGroups.TypeOf(group);
            if ((type == DiminishingType.Player && application.Target is Player) || type == DiminishingType.All)
            {
                tracker.Increment(group, system.NowMs);
            }
        }
    }

    public bool AcceptHolder(SpellApplication application, SpellAuraHolder holder)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(holder);
        if (application.GetState(this) is not Hit hit)
        {
            return true;
        }

        if (hit.Group != DiminishingGroup.None)
        {
            _holderGroups.AddOrUpdate(holder, new GroupBox(hit.Group));
        }

        int duration = holder.MaxDuration;
        if (duration <= 0)
        {
            return true;
        }

        int scaled = ScaleDuration(application, hit, duration);
        if (scaled == 0)
        {
            // Fully diminished: the holder is dropped; a channel of this spell on this target stops (Spell.cpp:1786-1796).
            SpellCast cast = application.Cast;
            if (cast.Caster.IsLikePlayer() && cast.State == SpellCastState.Casting && cast.Targets.Unit == application.Target.Guid)
            {
                application.System.InterruptCurrentCast(cast.Caster, spell => spell.Id == cast.Spell.Id);
            }

            _holderGroups.Remove(holder);
            return false;
        }

        if (scaled != duration)
        {
            holder.MaxDuration = scaled;
            holder.Duration = scaled;
        }

        return true;
    }

    /// <summary>
    /// vmangos Unit::ApplyDiminishingToDuration: no change for permanent auras, group none, or a friendly caster;
    /// otherwise the level's rate when the group is of type ALL, or of type PLAYER with both sides player-like.
    /// </summary>
    private static int ScaleDuration(SpellApplication application, Hit hit, int duration)
    {
        Unit caster = application.Cast.Caster;
        Unit target = application.Target;
        if (hit.Group == DiminishingGroup.None || application.System.Relations.IsFriendly(caster, target))
        {
            return duration;
        }

        DiminishingType type = DiminishingGroups.TypeOf(hit.Group);
        bool pvp = target.IsLikePlayer() && caster.IsLikePlayer();
        return (type == DiminishingType.Player && pvp) || type == DiminishingType.All
            ? (int)(duration * DiminishingGroups.RateOf(hit.Level))
            : duration;
    }

    private static bool AppliesAura(SpellInfo spell, int effectMask)
    {
        for (int i = 0; i < SpellConstants.MaxEffects; i++)
        {
            if ((effectMask & (1 << i)) != 0 && spell.Effects[i].Effect is SpellEffectName.ApplyAura or SpellEffectName.ApplyAreaAuraParty)
            {
                return true;
            }
        }

        return false;
    }

    private void OnHolder(SpellAuraHolder holder, bool applied)
    {
        if (_system is not null && _holderGroups.TryGetValue(holder, out GroupBox? box))
        {
            GetTracker(holder.Target).AuraChanged(box.Group, applied, _system.NowMs);
        }
    }

    private sealed record Hit(DiminishingGroup Group, DiminishingLevel Level);

    private sealed record GroupBox(DiminishingGroup Group);
}
