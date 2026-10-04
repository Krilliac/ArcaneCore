using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>Delays of the first five pushbacks of one cast in ms, then 200 for every further one (vmangos Spell::GetNextDelayAtDamageMsTime, Spell.h:401).</summary>
    private static int NextDelayMs(SpellCast cast)
    {
        if (cast.PushbackCount >= 5)
        {
            return 200;
        }

        return 1000 - (cast.PushbackCount++ * 200);
    }

    /// <summary>
    /// What damage does to the cast or channel a unit has in progress (vmangos Unit::DealDamage, Unit.cpp:900-947).
    /// Damage over time never interrupts or delays. Cast-bar spells are pushed back or cancelled only on players;
    /// a channel with the delay flag is shortened only on players; a channel with a damage-cancels flag stops for any unit.
    /// </summary>
    private void ApplyDamageToCurrentCast(Unit victim, UnitSpellState state, bool periodic)
    {
        if (periodic || state.CurrentCast is not { } cast)
        {
            return;
        }

        if (cast.State == SpellCastState.Preparing)
        {
            if (victim is not Player || cast.IsTriggered || cast.Timer <= 0)
            {
                return;
            }

            if (cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamageCancels))
            {
                Cancel(cast);
            }
            else
            {
                Delay(cast);
            }
        }
        else if (cast.State == SpellCastState.Casting)
        {
            uint flags = (uint)cast.Spell.ChannelInterruptFlags;
            if ((flags & SpellChannelInterruptFlags.Delay) != 0)
            {
                if (victim is Player)
                {
                    DelayChannel(cast);
                }
            }
            else if ((flags & (SpellChannelInterruptFlags.Damage | SpellChannelInterruptFlags.Damage2)) != 0)
            {
                Cancel(cast);
            }
        }
    }

    /// <summary>
    /// The talent "not lose casting time" chance and the RESIST_PUSHBACK auras (vmangos Spell::Delayed, Spell.cpp:7475-7481):
    /// the percent chance to ignore a pushback, starting from 0.
    /// </summary>
    private bool ResistsPushback(SpellCast cast)
    {
        float chance = SpellModifiers.Apply(cast.Caster, cast.Spell, SpellModOp.NotLoseCastingTime, 100f);
        chance += GetTotalAuraModifier(cast.Caster, AuraType.ResistPushback) - 100;
        return chance > 0f && Random.Next(0, 100) < chance;
    }

    /// <summary>
    /// vmangos Spell::Delayed (Spell.cpp:7465-7500): players only, spells with the damage-pushback flag only;
    /// the cast bar loses 1000, 800, 600, 400, 200, 200... ms per hit of this cast (never beyond the full cast
    /// time) unless the resist-pushback chance holds; SMSG_SPELL_DELAYED goes to the caster's own client.
    /// </summary>
    private void Delay(SpellCast cast)
    {
        if (cast.Caster is not Player player || !cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamagePushback) || ResistsPushback(cast))
        {
            return;
        }

        int delay = NextDelayMs(cast);
        if (cast.Timer + delay > cast.CastTime)
        {
            delay = cast.CastTime - cast.Timer;
            cast.Timer = cast.CastTime;
        }
        else
        {
            cast.Timer += delay;
        }

        player.Session.Send(WorldOpcode.SmsgSpellDelayed, SpellPackets.BuildSpellDelayed(player.Guid, (uint)delay));
    }

    /// <summary>
    /// vmangos Spell::DelayedChannel (Spell.cpp:7502-7545): the channel loses the same decaying delay (at most what
    /// is left), its auras on the caster and target are shortened by it, and the channel stops when nothing is left;
    /// otherwise MSG_CHANNEL_UPDATE tells the caster.
    /// </summary>
    private void DelayChannel(SpellCast cast)
    {
        if (cast.Caster is not Player player || ResistsPushback(cast))
        {
            return;
        }

        int delay = NextDelayMs(cast);
        if (cast.Timer < delay)
        {
            delay = cast.Timer;
            cast.Timer = 0;
        }
        else
        {
            cast.Timer -= delay;
        }

        Unit? target = ResolveUnitTarget(cast.Caster, cast.Targets);
        foreach (Unit unit in target is null || ReferenceEquals(target, cast.Caster) ? [cast.Caster] : new[] { cast.Caster, target })
        {
            if (IsQuestSettlementPending(unit) || GetState(unit.Guid) is not { } state
                || !ReferenceEquals(state.Unit, unit))
            {
                continue;
            }

            foreach (SpellAuraHolder holder in state.Auras.Where(h => h.Spell.Id == cast.Spell.Id
                && h.CasterGuid == cast.Caster.Guid && ReferenceEquals(ResolveAuraCaster(h), cast.Caster) && !h.IsPermanent))
            {
                holder.Duration = Math.Max(0, holder.Duration - delay);

                // vmangos Unit::DelaySpellAuraHolder: "push down the tick timer with the delay, otherwise we can still get max
                // ticks even with pushback" (RefreshAuraPeriodicTimers).
                foreach (SpellAura? aura in holder.Auras)
                {
                    if (aura is not null)
                    {
                        PeriodicTiming.SyncToDuration(aura, holder.Duration);
                    }
                }

                SendAuraDuration(holder);
            }
        }

        if (cast.Timer == 0)
        {
            Cancel(cast);
        }
        else
        {
            player.Session.Send(WorldOpcode.MsgChannelUpdate, SpellPackets.BuildChannelUpdate((uint)cast.Timer));
        }
    }

    /// <summary>
    /// SPELL_EFFECT_INTERRUPT_CAST (vmangos Spell::EffectInterruptCast, SpellEffects.cpp:3560-3600): a live target's
    /// cast with a cast bar (a channel, or a generic cast with a cast time) whose PreventionType is SILENCE is
    /// interrupted when it is a generic cast with the damage-pushback interrupt flag or a channel with the
    /// action-cancels flag; then the school is locked out for this spell's duration.
    /// </summary>
    private void EffectInterruptCast(SpellEffectContext context)
    {
        Unit target = context.Target;
        if (!target.IsAlive || GetState(target.Guid) is not { CurrentCast: { } cast } state || !ReferenceEquals(state.Unit, target))
        {
            return;
        }

        bool channel = cast.State == SpellCastState.Casting;
        bool generic = cast.State == SpellCastState.Preparing && cast.CastTime > 0;
        if ((!channel && !generic) || cast.Spell.PreventionType != SpellConstants.PreventionTypeSilence)
        {
            return;
        }

        bool interruptible = generic
            ? cast.Spell.InterruptFlags.HasFlag(SpellInterruptFlags.DamagePushback)
            : cast.Spell.ChannelInterruptFlags.HasFlag(SpellAuraInterruptFlags.Action);
        if (!interruptible)
        {
            return;
        }

        LockOut(target, SpellSchoolMasks.Of(cast.Spell.School), context.Spell.GetDuration(), cast.Spell);
        Cancel(cast);
    }

    /// <summary>
    /// vmangos SpellCaster::LockOutSpells (SpellCaster.cpp:2527-2534): each school of the mask is locked for
    /// <paramref name="durationMs"/>, an existing lockout is kept as it is (not extended), and a creature immune to
    /// silence ignores it (Creature.cpp:3272-3277). A player is told with a cooldown for every known spell of the
    /// school whose own cooldown ends earlier (Player::LockOutSpells, Player.cpp:22312-22345).
    /// </summary>
    internal void LockOut(Unit unit, uint schoolMask, int durationMs, SpellInfo interrupted)
    {
        if (durationMs <= 0 || GetState(unit.Guid) is not { } state)
        {
            return;
        }

        if (unit is Creatures.Creature && CreatureImmunities is { } creatures
            && (creatures.MechanicImmuneMask(unit) & SpellMechanics.Mask(SpellMechanic.Silence)) != 0)
        {
            return;
        }

        if (unit is Player player)
        {
            SendLockoutCooldowns(player, state, schoolMask, (uint)durationMs, interrupted);
        }

        for (int school = 0; school <= (int)SpellSchool.Arcane; school++)
        {
            if ((schoolMask & (1u << school)) != 0)
            {
                state.SchoolLockouts.TryAdd((SpellSchool)school, NowMs + (uint)durationMs);
            }
        }
    }

    private void SendLockoutCooldowns(Player player, UnitSpellState state, uint schoolMask, uint durationMs, SpellInfo interrupted)
    {
        uint now = NowMs;
        var entries = new List<(uint SpellId, uint Ms)>();
        if (Spellbook is null)
        {
            entries.Add((interrupted.Id, durationMs));
        }
        else
        {
            foreach (SpellInfo spell in Store.All)
            {
                if ((spell.SchoolMask() & schoolMask) == 0 || spell.HasAttribute(SpellAttributes.CooldownOnEvent) || !Spellbook.HasSpell(player, spell.Id))
                {
                    continue;
                }

                uint remaining = Math.Max(
                    state.SpellCooldowns.TryGetValue(spell.Id, out uint own) && own > now ? own - now : 0,
                    spell.Category != 0 && state.CategoryCooldowns.TryGetValue(spell.Category, out uint category) && category > now ? category - now : 0);
                if (remaining < durationMs)
                {
                    entries.Add((spell.Id, durationMs));
                }
            }
        }

        if (entries.Count > 0)
        {
            player.Session.Send(WorldOpcode.SmsgSpellCooldown, SpellPackets.BuildSpellCooldown(player.Guid, entries));
        }
    }

    /// <summary>Whether <paramref name="unit"/> is locked out of <paramref name="school"/> by an interrupt.</summary>
    public bool IsSchoolLocked(Unit unit, SpellSchool school)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return GetState(unit.Guid) is { } state && state.SchoolLockouts.TryGetValue(school, out uint until) && until > NowMs;
    }
}
