using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Pets;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// SPELL_EFFECT_RESURRECT (18) and SPELL_EFFECT_RESURRECT_NEW (113), after vmangos <c>Spell::EffectResurrect</c> (SpellEffects.cpp:5228-5251)
/// and <c>Spell::EffectResurrectNew</c> (:209-263): neither resurrects anybody; they offer the dead player a resurrection
/// (<see cref="Death.Resurrection.ResurrectionService.Request"/>), which it accepts or declines.
/// <list type="bullet">
/// <item>RESURRECT_NEW (all the player spells: Resurrection, Redemption, Ancestral Spirit, Rebirth) offers health = the effect value and
/// mana = the effect's misc value.</item>
/// <item>RESURRECT (a few old and item spells: the Defibrillate and Smelling Salts items, Revive) offers the effect value percent of the
/// target's maximum health and mana, dithered to a whole number.</item>
/// </list>
/// A target that is alive, not a player, or already holding a request is skipped. The cast check (Spell.cpp:5780-5790): an explicit
/// corpse must be in the caster's map (else bad targets) and in its line of sight unless the spell ignores it.
/// RESURRECT_NEW on a creature target is the dead current pet's revival (Revive Pet, SpellEffects.cpp:216-256).
/// </summary>
public sealed class ResurrectEffects : ISpellHandlerModule
{
    /// <summary>SPELL_ATTR_EX3_NO_RES_TIMER (AttributesEx3 0x10): the resurrection happens at once (Rebirth), without the client's timer.</summary>
    public const uint Ex3NoResTimer = 0x00000010;

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterEffect(SpellEffectName.ResurrectNew, EffectResurrectNew);
        system.RegisterEffect(SpellEffectName.Resurrect, EffectResurrect);
        system.RegisterEffectCheck(SpellEffectName.ResurrectNew, CheckCorpse);
        system.RegisterEffectCheck(SpellEffectName.Resurrect, CheckCorpse);
    }

    private static void EffectResurrectNew(SpellEffectContext context)
    {
        // SpellEffects.cpp:216-256: a dead current pet (Revive Pet) is restored with the effect value as health; the owner then loses every
        // spell that carries Demonic Sacrifice's override script (2228), its other override-class scripts stay.
        if (context.Target is Creatures.Creature pet)
        {
            if (pet.System?.TryReviveCurrentPet(pet, context.Caster, unchecked((uint)context.Value), context.System) == true
                && pet.GetOwner() is { } owner)
            {
                uint[] sacrificeSpells = context.System.GetAuras(owner)
                    .Where(holder => holder.Auras.Any(aura => aura is { Type: AuraType.OverrideClassScripts, MiscValue: 2228 }))
                    .Select(holder => holder.Spell.Id).Distinct().ToArray();
                foreach (uint spell in sacrificeSpells)
                {
                    context.System.RemoveAuras(owner, spell);
                }
            }

            return;
        }

        if (context.Target is not Player target || target.Combat.DeathState == DeathState.Alive || !target.IsInWorld)
        {
            return;
        }

        Offer(context, target, health: (uint)Math.Max(context.Value, 0), mana: (uint)Math.Max(context.Effect.MiscValue, 0));
    }

    private static void EffectResurrect(SpellEffectContext context)
    {
        if (context.Target is not Player target || target.Combat.DeathState == DeathState.Alive || !target.IsInWorld)
        {
            return;
        }

        // SpellEffects.cpp:5246-5247: GetMaxHealth() * damage / 100 is uint32 arithmetic (damage converts to unsigned), so the
        // value handed to rand_ditheru(float) has no fraction and the dither never rounds up.
        uint percent = (uint)Math.Max(context.Value, 0);
        uint health = unchecked(target.MaxHealth * percent) / 100;
        uint mana = unchecked(MapCombat.GetMaxPower(target, PowerType.Mana) * percent) / 100;
        Offer(context, target, health, mana);
    }

    private static void Offer(SpellEffectContext context, Player target, uint health, uint mana)
    {
        if (context.System.Resurrection is not { } requests)
        {
            return;
        }

        Unit caster = context.Caster;
        bool spiritHealer = caster is not Player && (caster.GetUInt32(UpdateFields.UnitNpcFlags) & (uint)NpcFlags.SpiritHealer) != 0;
        string name = caster is Player ? string.Empty : (caster as Creatures.Creature)?.Template.Name ?? string.Empty;
        requests.Request(target, caster, name, health, mana, sickness: spiritHealer, noResTimer: (context.Spell.AttributesEx3 & Ex3NoResTimer) != 0);
    }

    private static SpellCastResult CheckCorpse(SpellEffectCheckContext context)
    {
        if (context.Targets.Corpse.IsEmpty)
        {
            return SpellCastResult.CastOk;
        }

        if (context.Caster.Map is not { } map || map.FindObject(context.Targets.Corpse) is not Corpse corpse)
        {
            return SpellCastResult.BadTargets;
        }

        return !context.Spell.HasAttribute(SpellAttributesEx2.IgnoreLineOfSight) && !map.Collision.IsWithinLineOfSight(context.Caster, corpse)
            ? SpellCastResult.LineOfSight
            : SpellCastResult.CastOk;
    }
}
