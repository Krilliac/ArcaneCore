using System.Runtime.CompilerServices;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Locomotion;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Spells.Druid;

namespace ArcaneCore.Game.Spells;

/// <summary>The display a Transform aura gives its target and the transform scale it sets (vmangos ChooseDisplayId's display and <c>*scale</c>).</summary>
public readonly record struct TransformDisplay(uint DisplayId, float Scale);

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

    /// <summary>
    /// The display and the transform scale of the creature template <paramref name="creatureEntry"/> (vmangos <c>ChooseDisplayId</c> with its
    /// <c>scale</c> out parameter: the template's display scale, else the display's model scale), or null when the template does not exist.
    /// The default asks <see cref="FindDisplay"/> and keeps the scale at 1.
    /// </summary>
    TransformDisplay? FindTransform(uint creatureEntry) => FindDisplay(creatureEntry) is { } display ? new TransformDisplay(display, 1f) : null;
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
/// SPELL_AURA_TRANSFORM (56), after vmangos <c>Aura::HandleAuraTransform</c> (SpellAuras.cpp:2632-2780; mangoszero SpellAuraShapeshift.cpp:502-598
/// is the cross-check): the misc value is the creature entry whose display the target wears (Polymorph, the transform items). An unknown entry
/// gives the box model (UNIT_DISPLAY_ID_BOX, 4; mangoszero uses the pink pig 16358 instead). A misc value of 0 is only defined for Orb of
/// Deception (16739), which maps the wearer's native display to the matching Orb model; any other spell without an entry logs through the
/// display source and changes nothing.
/// <para>
/// The unit remembers its active transform holder (<see cref="ActiveHolder"/>, vmangos <c>GetTransForm</c>; world thread, a static weak table
/// keyed by the unit like <see cref="TransformScale"/>, so a unit that leaves the world takes it along). A transform applies (display and
/// record) only when none is active, when it is negative, or when the active one is positive: a positive transform over a negative one (a
/// mage polymorphed who drinks a transform potion) changes nothing, as vmangos. Removing a transform only resets the cosmetics when it is the
/// active one: the display goes back to the native one and then one of the transforms still on the unit is applied again, a negative one by
/// preference, else the latest (<c>GetAurasByType</c> walked from the back); with none left, the shapeshift form's display and scale come
/// back (vmangos <c>GetShapeshiftDisplayInfo</c>). This runs when an aura is applied or removed, never per tick; the only lookup is the
/// display source's. A creature entry also sets the transform scale (<see cref="TransformScale"/>, vmangos <c>SetTransformScale</c> with
/// <c>ChooseDisplayId</c>'s scale; the box takes scale 1) and a reset returns to the native scale (<c>ResetTransformScale</c>). When the spell
/// system has a display model resolver the bounding radius and combat reach follow the new display (vmangos <c>SetDisplayId</c> then
/// <c>UpdateModelData</c>).
/// </para>
/// <para>
/// The shapeshift forms skip their display while a Transform aura is on the unit (<see cref="ShapeshiftService"/>), see
/// docs/areas/transform-and-charge.md. LIMIT: Orb of Deception keeps the unit's scale (vmangos adjusts it for taurens and gnomes).
/// </para>
/// </summary>
public sealed class TransformAuras : ISpellHandlerModule
{
    /// <summary>The box model of an unknown creature entry (vmangos <c>UNIT_DISPLAY_ID_BOX</c>).</summary>
    public const uint BoxDisplay = Creature.DisplayIdBox;

    /// <summary>Orb of Deception (SpellAuras.cpp:2647): the only misc value 0 transform with a built-in model table.</summary>
    public const uint OrbOfDeception = 16739;

    // Orb of Deception: native display -> Orb display (SpellAuras.cpp:2647-2704 by race and gender; mangoszero SpellAuraShapeshift.cpp:514-545).
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

    /// <summary>The holder whose transform is recorded as the unit's active one (vmangos <c>GetTransForm</c>), or null.</summary>
    public static SpellAuraHolder? ActiveHolder(Unit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return s_active.TryGetValue(unit, out ActiveTransform? state) ? state.Holder : null;
    }

    private static void Apply(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        if (apply)
        {
            // "update active transform spell only not set or not overwriting negative by positive case"
            ActiveTransform state = s_active.GetOrCreateValue(holder.Target);
            if (state.Holder is null || !holder.IsPositive || state.Holder.IsPositive)
            {
                Transform(system, holder, aura);
                state.Holder = holder;
            }

            return;
        }

        // "reset cosmetics only if it's the current transform"
        if (s_active.TryGetValue(holder.Target, out ActiveTransform? active) && ReferenceEquals(active.Holder, holder))
        {
            Restore(system, active, holder.Target);
        }
    }

    private static void Transform(SpellSystem system, SpellAuraHolder holder, SpellAura aura)
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
            // "Unknown creature id (only need its display_id)": the box at scale 1, as vmangos. With no source registered (no creature data
            // in this world) every entry is unknown.
            TransformDisplay display = source?.FindTransform((uint)aura.MiscValue) ?? new TransformDisplay(BoxDisplay, 1f);
            target.DisplayId = display.DisplayId;
            TransformScale.Set(target, display.Scale > 0 && float.IsFinite(display.Scale) ? display.Scale : 1f, system);
        }

        // Equipment of a creature target (LoadEquipment of the template's EquipmentTemplateId) is not applied: see the lane doc.
        UpdateModel(system, target);
    }

    private static void Restore(SpellSystem system, ActiveTransform state, Unit target)
    {
        state.Holder = null;
        target.DisplayId = target.NativeDisplayId;
        TransformScale.Reset(target, system); // ResetTransformScale

        // "re-apply some from still active with preference negative cases": the removed holder has already left the list. vmangos walks
        // GetAurasByType(SPELL_AURA_TRANSFORM) from the back, so without a negative one the latest transform wins.
        SpellAuraHolder? chosen = null;
        SpellAura? chosenAura = null;
        List<SpellAuraHolder> holders = [.. system.GetAuras(target)];
        for (int i = holders.Count - 1; i >= 0; i--)
        {
            SpellAuraHolder other = holders[i];
            if (other.IsRemoved)
            {
                continue;
            }

            foreach (SpellAura? candidate in other.AuraSpan)
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
            Transform(system, chosen, chosenAura);
            state.Holder = chosen;
            return;
        }

        // "reapply shapeshifting, there should be only one": the form's display and scale (GetShapeshiftDisplayInfo).
        ShapeshiftForm form = ShapeshiftService.GetForm(target);
        if (form != ShapeshiftForm.None
            && FormDisplayTable.Get((byte)form, target is not Player player || player.Team == Team.Alliance) is { } display)
        {
            TransformScale.Set(target, display.Scale, system);
            target.DisplayId = display.DisplayId;
        }

        UpdateModel(system, target);
    }

    /// <summary>vmangos <c>Unit::SetDisplayId</c> then <c>UpdateModelData</c>, when the world gave the spell system its display model data.</summary>
    private static void UpdateModel(SpellSystem system, Unit target)
    {
        if (system.DisplayModelResolver is not null)
        {
            system.UpdateDisplayModel(target);
        }
    }
}
