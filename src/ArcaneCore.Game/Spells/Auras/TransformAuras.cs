using System.Runtime.CompilerServices;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// Resolves the creature entry a Transform aura names to the display id the target wears. The creature data lives in the world
/// daemon, which registers its source with <see cref="TransformDisplays.Register"/> (the same shape as the mount display source,
/// <see cref="IMountDisplaySource"/>, but without the mount rule's gender swap: a transform takes the template's display, as vmangos
/// <c>Creature::ChooseDisplayId</c> does).
/// </summary>
public interface ITransformDisplaySource
{
    /// <summary>The display id of the creature template <paramref name="creatureEntry"/>, or null when the template does not exist (the source logs it).</summary>
    uint? FindDisplay(uint creatureEntry);

    /// <summary>A Transform aura without a creature entry (misc value 0) whose spell has no custom model; the source logs it, the target keeps its display.</summary>
    void ReportNoModel(uint spellId);
}

/// <summary>The per-world registry of the <see cref="ITransformDisplaySource"/> (written at startup, read on the world thread).</summary>
public static class TransformDisplays
{
    private static readonly ConditionalWeakTable<WorldRuntime, ITransformDisplaySource> s_sources = new();

    /// <summary>Use <paramref name="source"/> to resolve Transform auras' creature entries in <paramref name="world"/> (call before the world thread starts).</summary>
    public static void Register(WorldRuntime world, ITransformDisplaySource source)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(source);
        s_sources.AddOrUpdate(world, source);
    }

    /// <summary>The source of the world <paramref name="map"/> belongs to, or null when none is registered.</summary>
    public static ITransformDisplaySource? For(Map? map)
        => map?.FindUpdater<MapLocomotion>()?.World is { } world && s_sources.TryGetValue(world, out ITransformDisplaySource? source) ? source : null;
}

/// <summary>
/// SPELL_AURA_TRANSFORM (56), after mangoszero <c>Aura::HandleAuraTransform</c> (SpellAuraShapeshift.cpp:502-598): the misc value is
/// the creature entry whose display the target wears (Polymorph, the transform items). An unknown entry gives the pink pig (16358,
/// "pig pink ^_^"). A misc value of 0 is only defined for Orb of Deception (16739), which maps the wearer's native display to the
/// matching Orb model; any other spell without an entry logs through the display source and changes nothing.
/// <para>
/// The unit remembers its active transform holder (<see cref="ActiveHolder"/>, world thread, a static weak table keyed by the unit like <see cref="TransformScale"/>, so a unit that
/// leaves the world takes it along). A new transform always sets the display, but only becomes the active one when none is set, when it is
/// negative, or when the active one is positive: a positive transform over a negative one (a mage polymorphed who drinks a transform
/// potion) changes the model without taking the record. Removing a transform resets the display to the native one and then re-applies
/// one of the transforms that are still on the unit, a negative one by preference (<c>GetAurasByType</c> order), so a second
/// transform overrides the first and the first comes back when the second ends. This runs when an aura is applied or removed, never
/// per tick; the only lookup is the display source's.
/// </para>
/// <para>
/// The shapeshift forms skip their display while a Transform aura is on the unit (<see cref="ShapeshiftService"/>); a transform that
/// ends under a form restores the native display, not the form's (as mangoszero), see docs/areas/transform-and-charge.md.
/// </para>
/// </summary>
public sealed class TransformAuras : ISpellHandlerModule
{
    /// <summary>The pink pig model of an unknown creature entry (mangoszero <c>model_id = 16358</c>).</summary>
    public const uint PigDisplay = 16358;

    /// <summary>Orb of Deception (SpellAuraShapeshift.cpp:511): the only misc value 0 transform with a built-in model table.</summary>
    public const uint OrbOfDeception = 16739;

    // Orb of Deception: native display -> Orb display (SpellAuraShapeshift.cpp:514-545).
    private static readonly Dictionary<uint, uint> s_orbDisplays = new()
    {
        [1479] = 10134, // troll female
        [1478] = 10135, // troll male
        [59] = 10136,   // tauren male
        [49] = 10137,   // human male
        [50] = 10138,   // human female
        [51] = 10139,   // orc male
        [52] = 10140,   // orc female
        [53] = 10141,   // dwarf male
        [54] = 10142,   // dwarf female
        [55] = 10143,   // night elf male
        [56] = 10144,   // night elf female
        [58] = 10145,   // undead female
        [57] = 10146,   // undead male
        [60] = 10147,   // tauren female
        [1563] = 10148, // gnome male
        [1564] = 10149, // gnome female
    };

    private sealed class ActiveTransform
    {
        public SpellAuraHolder? Holder { get; set; }
    }

    private static readonly ConditionalWeakTable<Unit, ActiveTransform> s_active = new();

    public void Register(SpellSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        system.RegisterAura(AuraType.Transform, new AuraHandler(Apply, null));
    }

    /// <summary>The holder whose transform is recorded as the unit's active one (vmangos <c>GetTransform</c>), or null.</summary>
    public static SpellAuraHolder? ActiveHolder(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return s_active.TryGetValue(unit, out ActiveTransform? state) ? state.Holder : null;
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply)
        {
            Transform(holder, aura);
            return;
        }

        Restore(system, holder.Target);
    }

    private static void Transform(SpellAuraHolder holder, SpellAura aura)
    {
        Unit target = holder.Target;
        ITransformDisplaySource? source = TransformDisplays.For(target.Map);
        if (aura.MiscValue == 0)
        {
            if (holder.Spell.Id == OrbOfDeception)
            {
                if (s_orbDisplays.TryGetValue(target.NativeDisplayId, out uint orb))
                {
                    target.DisplayId = orb;
                }
            }
            else
            {
                source?.ReportNoModel(holder.Spell.Id);
            }
        }
        else
        {
            // "Auras: unknown creature id = %d (only need its modelid)": the pig, as the reference does. With no source registered
            // (no creature data in this world) every entry is unknown.
            target.DisplayId = source?.FindDisplay((uint)aura.MiscValue) ?? PigDisplay;
        }

        // Equipment of a creature target (LoadEquipment of the template's EquipmentTemplateId) is not applied: see the lane doc.
        ActiveTransform state = s_active.GetOrCreateValue(target);
        if (state.Holder is null || !holder.IsPositive || state.Holder.IsPositive)
        {
            state.Holder = holder;
        }
    }

    private static void Restore(SpellSystem system, Unit target)
    {
        if (s_active.TryGetValue(target, out ActiveTransform? state))
        {
            state.Holder = null;
        }

        target.DisplayId = target.NativeDisplayId;

        // "re-apply some from still active with preference negative cases": the removed holder has already left the list.
        SpellAuraHolder? chosen = null;
        SpellAura? chosenAura = null;
        foreach (SpellAuraHolder other in system.GetAuras(target))
        {
            if (other.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? candidate in other.Auras)
            {
                if (candidate is null || candidate.Type != AuraType.Transform)
                {
                    continue;
                }

                if (chosen is null || (!other.IsPositive && chosen.IsPositive))
                {
                    chosen = other;
                    chosenAura = candidate;
                }
            }

            if (chosen is { IsPositive: false })
            {
                break;
            }
        }

        if (chosen is not null && chosenAura is not null)
        {
            Transform(chosen, chosenAura);
        }
    }
}
