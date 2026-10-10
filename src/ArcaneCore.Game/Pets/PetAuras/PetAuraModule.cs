using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;

namespace ArcaneCore.Game.Pets.PetAuras;

/// <summary>
/// Wires the owner-to-pet auras into a spell system (discovered <see cref="ISpellHandlerModule"/>):
/// <list type="bullet">
/// <item>the DUMMY aura case of every <see cref="PetAuraTable"/> spell (vmangos Aura::HandleAuraDummy, SpellAuras.cpp:2201-2208): applied, the aura's
/// target (the owner) adds the pet aura; removed, it drops it;</item>
/// <item>an unlearn observer (vmangos Player::RemoveSpell, Player.cpp:3850-3852): a player who loses a table spell drops its pet aura, which is how a
/// castable one (Soul Link's DUMMY effect) ends;</item>
/// <item>SPELL_EFFECT_APPLY_AREA_AURA_PET, which every pet aura of the table is (<see cref="PetAreaAura"/>).</item>
/// </list>
/// </summary>
public sealed class PetAuraModule : ISpellHandlerModule
{
    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        PetAuraService service = PetAuraService.For(system);
        foreach (uint spellId in PetAuraTable.Entries.Keys)
        {
            system.RegisterDummyAuraHandler(spellId, (_, holder, _, apply) =>
            {
                if (apply)
                {
                    service.AddPetAura(holder.Target, holder.Spell.Id);
                }
                else
                {
                    service.RemovePetAura(holder.Target, holder.Spell.Id);
                }
            });
        }

        system.AddLearnObserver(new UnlearnObserver(service));
        PetAreaAura.Install(system);
    }

    private sealed class UnlearnObserver(PetAuraService service) : ISpellLearnObserver
    {
        public void AfterRemove(Player player, uint spellId)
        {
            if (PetAuraTable.Find(spellId) is not null)
            {
                service.RemovePetAura(player, spellId);
            }
        }
    }
}

/// <summary>
/// The DUMMY effect half of the pet auras (vmangos Spell::EffectDummy, SpellEffects.cpp:1497-1502): when a table spell's DUMMY effect runs, its caster adds
/// the pet aura. Of the vanilla rows only Soul Link (19028, a DUMMY effect at TARGET_UNIT_CASTER_PET) is an effect; the rest are DUMMY auras.
/// </summary>
[SpellScript(PetAuraTable.SoulLink)]
public sealed class PetAuraDummyEffectScript : ISpellScript
{
    public void OnEffectExecute(SpellEffectContext context)
    {
        if (context.Effect.Effect == SpellEffectName.Dummy && PetAuraTable.Find(context.Spell.Id) is not null)
        {
            PetAuraService.For(context.System).AddPetAura(context.Caster, context.Spell.Id);
        }
    }
}

/// <summary>
/// SPELL_EFFECT_APPLY_AREA_AURA_PET (119; vmangos Spell::EffectApplyAreaAura and AreaAura::Update, SpellAuras.cpp:690-706): the caster carries the
/// aura as its source and its owner, within the effect radius (100 yards for the pet auras), gets the same aura from it.
/// <para>
/// LIMITS: the owner's copy is decided when the source is applied and goes with the source (when the pet loses it, dies or is unsummoned, or leaves the
/// owner's map); vmangos re-checks the radius every update, which is not done here.
/// </para>
/// </summary>
public static class PetAreaAura
{
    /// <summary>Install the effect (when no other handler has claimed it) and the owner half.</summary>
    public static void Install(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        if (system.HasEffectHandler(SpellEffectName.ApplyAreaAuraPet))
        {
            return;
        }

        system.RegisterEffect(SpellEffectName.ApplyAreaAuraPet, context =>
        {
            // vmangos applies an area aura to the caster only; the others get theirs from the source.
            if (ReferenceEquals(context.Target, context.Caster))
            {
                context.System.ApplyAuraEffect(context);
            }
        });
        system.HolderAdded += holder => SpreadToOwner(system, holder);
    }

    private static void SpreadToOwner(SpellSystem system, SpellAuraHolder source)
    {
        if (source.IsRemoved || source.AreaParent is not null || source.CasterGuid != source.Target.Guid
            || !source.Spell.HasEffect(SpellEffectName.ApplyAreaAuraPet) || source.Target.GetOwner() is not { IsAlive: true } owner
            || ReferenceEquals(owner, source.Target) || !ReferenceEquals(owner.Map, source.Target.Map))
        {
            return;
        }

        float radius = source.Spell.Effects.Where(e => e.Effect == SpellEffectName.ApplyAreaAuraPet).Select(e => e.Radius).DefaultIfEmpty(0).Max();
        float dx = owner.X - source.Target.X;
        float dy = owner.Y - source.Target.Y;
        float dz = owner.Z - source.Target.Z;
        if ((dx * dx) + (dy * dy) + (dz * dz) > radius * radius
            || system.GetAuras(owner).Any(h => !h.IsRemoved && h.Spell.Id == source.Spell.Id))
        {
            return;
        }

        var child = new SpellAuraHolder(source.Spell, owner, source.CasterGuid, source.CasterLevel, source.CasterOwner,
            source.IsPermanent ? -1 : Math.Max(source.Duration, 1))
        {
            AreaParent = source,
        };
        foreach (SpellAura? aura in source.AuraSpan)
        {
            if (aura is not null && source.Spell.Effects[aura.EffectIndex].Effect == SpellEffectName.ApplyAreaAuraPet)
            {
                child.SetAura(new SpellAura(aura.EffectIndex, aura.Type, aura.Amount, aura.Amplitude, aura.MiscValue, owner.PowerType));
            }
        }

        if (child.IsEmpty)
        {
            return;
        }

        source.AreaChildren[owner.Guid] = child;
        system.AddAuraHolder(child);
    }
}
