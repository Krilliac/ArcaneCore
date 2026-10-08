using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_AURA_SPIRIT_OF_REDEMPTION (176) and the priest talent behind it (docs/areas/unit-control.md). vmangos Unit::Kill (Unit.cpp:1108-1140):
/// a priest with the talent (20711) who takes a killing blow from anything but the spirit's own Suicide (27965) does not die: casts are
/// interrupted, the self-resurrection spell is kept, the auras go as on a death, and 27827 is cast with the maximum health as its heal. 27827
/// heals, gives water breathing and the Spirit of Redemption form (32), whose linked spells 27792 and 27795 (ShapeshiftService) carry the
/// unattackable, free-spell, root, pacify, no-regeneration and this aura. Aura::HandleSpiritOfRedemption (SpellAuras.cpp:5699-5738): on apply a
/// sitting player stands, mana is filled and damage can no longer take health (invincibility threshold = maximum health); "Untransform Hero"
/// (25100) is cast a batching interval later. On removal (the form ends with 27827's 10 s) the spirit is stunned and, a batching interval later,
/// unstunned, made mortal again and killed by Suicide.
/// </summary>
public sealed class SpiritOfRedemptionModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.SpiritOfRedemption, new AuraHandler(static (s, h, a, apply) => s.OnSpiritOfRedemptionAura(h, apply), null));
    }
}

public sealed partial class SpellSystem
{
    /// <summary>The priest talent (a dummy aura) that makes a killing blow cast <see cref="SpiritOfRedemptionSpell"/>.</summary>
    public const uint SpiritOfRedemptionTalent = 20711;

    /// <summary>The spell cast instead of the death (heal to full, water breathing, the Spirit of Redemption form).</summary>
    public const uint SpiritOfRedemptionSpell = 27827;

    /// <summary>"Suicide": the instakill that ends the spirit; its damage is the only one the talent lets through.</summary>
    public const uint SpiritOfRedemptionSuicide = 27965;

    /// <summary>"Untransform Hero", the visual cast a batching interval after the spirit appears.</summary>
    public const uint UntransformHero = 25100;

    /// <summary>vmangos BATCHING_INTERVAL (Object.h): the delay of the spirit's two follow-up steps.</summary>
    public const uint BatchingIntervalMs = 400;

    /// <summary>
    /// The Spirit of Redemption half of vmangos Unit::Kill (Unit.cpp:1108-1140): true when the killing blow made a priest with the talent
    /// a spirit instead of killing it (the caller then leaves health and death state alone).
    /// </summary>
    internal bool TryEnterSpiritOfRedemption(Unit victim, SpellInfo? killingSpell)
    {
        if (victim is not Player { Class: Class.Priest } priest || killingSpell?.Id == SpiritOfRedemptionSuicide
            || !HasAura(priest, SpiritOfRedemptionTalent) || Store.Get(SpiritOfRedemptionSpell) is null)
        {
            return false;
        }

        InterruptNonMeleeSpells(priest);

        // save the self-resurrection spell before the aura removal, restore it for the real death
        Death.Resurrection.SelfResurrection.OnPlayerDied(this, priest);
        uint selfRes = priest.GetUInt32(UpdateFields.PlayerSelfResSpell);
        RemoveAllAurasOnDeath(priest);
        priest.SetUInt32(UpdateFields.PlayerSelfResSpell, selfRes);

        CastCustomSpell(priest, SpiritOfRedemptionSpell, SpellCastTargets.ForSelf(), (int)priest.MaxHealth);
        return true;
    }

    /// <summary>vmangos Unit::RemoveAllAurasOnDeath for a unit that stays alive: every holder that is neither passive nor death persistent.</summary>
    private void RemoveAllAurasOnDeath(Unit unit)
    {
        if (GetState(unit.Guid) is not { } state || !ReferenceEquals(state.Unit, unit))
        {
            return;
        }

        foreach (SpellAuraHolder holder in state.Auras.Where(h => !h.Spell.IsPassive && !h.Spell.IsDeathPersistent).ToArray())
        {
            RemoveHolder(state, holder, AuraRemoveMode.Death);
        }
    }

    /// <summary>vmangos Aura::HandleSpiritOfRedemption (SpellAuras.cpp:5699-5738), the real apply and remove.</summary>
    internal void OnSpiritOfRedemptionAura(SpellAuraHolder holder, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            // set stand state (expected in this form)
            if (target is Player player && player.StandState != StandState.Stand)
            {
                player.SetStandState(StandState.Stand);
            }

            // set health and mana to maximum
            SetPower(target, PowerType.Mana, target.GetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Mana));
            target.InvincibilityHpThreshold = target.MaxHealth;

            // cast visual on next tick
            AfterBatchingInterval(target, () =>
            {
                if (target.IsInWorld && Store.Get(UntransformHero) is not null)
                {
                    CastSpell(target, UntransformHero, SpellCastTargets.ForSelf(), triggered: true);
                }
            });
            return;
        }

        // die at aura end
        target.UnitFlags |= UnitFlags.Stunned;
        AfterBatchingInterval(target, () =>
        {
            target.UnitFlags &= ~UnitFlags.Stunned;
            target.InvincibilityHpThreshold = 0;
            if (target.IsInWorld && target.IsAlive && Store.Get(SpiritOfRedemptionSuicide) is not null)
            {
                CastSpell(target, SpiritOfRedemptionSuicide, SpellCastTargets.ForSelf(), triggered: true);
            }
        });
    }

    /// <summary>vmangos m_Events.AddLambdaEventAtOffset(..., BATCHING_INTERVAL): run on the unit's map after the interval (at once outside a map).</summary>
    private static void AfterBatchingInterval(Unit unit, Action action)
    {
        if (unit.Map?.FindUpdater<MapUnitControl>() is { } control)
        {
            control.Schedule(unit, BatchingIntervalMs, action);
        }
        else
        {
            action();
        }
    }
}
