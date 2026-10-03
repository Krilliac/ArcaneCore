using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>SPELL_ATTR_EX2_NO_TARGET_PER_SECOND_COSTS (vmangos SpellDefines.h:917).</summary>
    private const uint NoTargetPerSecondCostsFlag = 0x00000800;

    /// <summary>
    /// The per-second power cost of a spell with <see cref="SpellInfo.ManaPerSecond"/> (Health Funnel, 5 health per second at rank 1), after
    /// vmangos SpellAuraHolder::Update (SpellAuras.cpp:7296-7330): every second of a running holder (a duration above 0), the caster pays
    /// <c>manaPerSecond + manaPerSecondPerLevel * level</c> of the spell's power type (health for -2). A spell with the
    /// "no target per second costs" attribute only pays when the caster's selected target is the caster, so a spell that puts holders on several units
    /// is not paid once per holder. When the caster cannot pay (health must stay above the cost, other powers need at least the cost) the aura
    /// goes, the caster's channel ends and a player gets SPELL_FAILED_FIZZLE. The timer is reset to a second after a payment and keeps running
    /// while the caster is not in the world.
    /// <para>
    /// LIMIT, deliberate: for a health cost with health at or below the amount vmangos falls through to <c>GetPower(POWER_HEALTH)</c>, which
    /// indexes an update field that is not the health (POWER_HEALTH is 0xFFFFFFFE); the evident intent, a fizzle, is what happens here.
    /// </para>
    /// </summary>
    private void ChargePerSecondCost(SpellAuraHolder holder, uint diffMs)
    {
        SpellInfo spell = holder.Spell;
        if (spell.ManaPerSecond == 0 && spell.ManaPerSecondPerLevel == 0)
        {
            return;
        }

        holder.PerSecondTimer -= (int)diffMs;
        if (holder.PerSecondTimer > 0 || ResolveAuraCaster(holder) is not { } caster)
        {
            return;
        }

        holder.PerSecondTimer = 1000;
        int perSecond = (int)spell.ManaPerSecond + ((int)spell.ManaPerSecondPerLevel * caster.Level);
        Unit target = holder.Target;
        if (perSecond == 0 || ((uint)spell.AttributesEx2 & NoTargetPerSecondCostsFlag) != 0 && holder.CasterGuid != target.Target)
        {
            return;
        }

        if (spell.PowerType == SpellMath.PowerHealth)
        {
            if (caster.Health > perSecond)
            {
                caster.Health -= (uint)perSecond;
                return;
            }
        }
        else if (spell.PowerType is >= 0 and <= (int)PowerType.Happiness && GetPower(caster, (PowerType)spell.PowerType) >= perSecond)
        {
            SetPower(caster, (PowerType)spell.PowerType, GetPower(caster, (PowerType)spell.PowerType) - (uint)perSecond);
            return;
        }

        RemoveAuras(target, spell.Id);
        if (GetState(caster.Guid)?.CurrentCast is { } cast && cast.Spell.Id == spell.Id && spell.IsChanneled)
        {
            Interrupt(cast);
        }

        if (caster is Player player)
        {
            player.Session.Send(WorldOpcode.SmsgCastResult, SpellPackets.BuildCastResult(spell.Id, SpellCastResult.Fizzle));
        }
    }
}
