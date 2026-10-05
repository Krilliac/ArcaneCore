using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.Spells;

/// <summary>vmangos Aura::HandleAuraTransform selection, display restoration, and polymorph identity.</summary>
public sealed class TransformAuras : ISpellHandlerModule
{
    public void Register(SpellSystem system)
        => system.RegisterAura(AuraType.Transform, new AuraHandler(ApplyTransform, null));

    private static void ApplyTransform(SpellSystem system, SpellAuraHolder holder, SpellAura aura, bool apply)
    {
        Unit target = holder.Target;
        if (apply)
        {
            SpellAuraHolder? current = Current(system, target);
            if (current is null || !holder.IsPositive || current.IsPositive)
            {
                // Negative transforms may replace a positive transform; a positive transform
                // never overwrites an active negative one.
                ApplyActive(system, target, holder, aura);
            }
            return;
        }

        if (!ReferenceEquals(target.TransformHolder, holder))
        {
            return;
        }

        target.TransformSpellId = 0;
        target.TransformHolder = null;
        target.DisplayId = target.TransformBaseDisplayId != 0 ? target.TransformBaseDisplayId : target.NativeDisplayId;
        target.TransformScale = 1.0f;
        target.SetFloat(UpdateFields.ObjectFieldScaleX, target.TransformBaseScale * ActiveScaleFactor(system, target));
        system.UpdateDisplayModel(target);

        SpellAuraHolder? replacement = system.GetAuras(target)
            .Select((h, index) => (Holder: h, Index: index))
            .Where(x => !x.Holder.IsRemoved && !ReferenceEquals(x.Holder, holder)
                && x.Holder.Auras.Any(a => a?.Type == AuraType.Transform))
            .OrderByDescending(x => x.Holder.IsPositive ? 0 : 1)
            .ThenByDescending(x => x.Index)
            .Select(x => x.Holder)
            .FirstOrDefault();
        if (replacement is not null && replacement.Auras.FirstOrDefault(a => a?.Type == AuraType.Transform) is { } replacementAura)
        {
            ApplyActive(system, target, replacement, replacementAura);
        }
        else
        {
            RestoreFormVisual(system, target);
        }
    }

    private static void ApplyActive(SpellSystem system, Unit target, SpellAuraHolder holder, SpellAura aura)
    {
        if (target.TransformSpellId == 0)
        {
            target.TransformBaseDisplayId = target.FormDisplayId != 0 ? target.NativeDisplayId : target.DisplayId;
            target.TransformBaseScale = target.FormDisplayId != 0 && target.FormBaseScale != 0
                ? target.FormBaseScale
                : target.GetFloat(UpdateFields.ObjectFieldScaleX) / ActiveScaleFactor(system, target);
            target.SetFloat(UpdateFields.ObjectFieldScaleX, target.TransformBaseScale * ActiveScaleFactor(system, target));
        }
        else
        {
            // Rebase a replacement on the pre-transform object scale while retaining
            // any active ModScale aura factors.
            target.SetFloat(UpdateFields.ObjectFieldScaleX, target.TransformBaseScale * ActiveScaleFactor(system, target));
        }

        TransformDisplay display = system.ResolveTransformDisplay(target, holder, aura);
        target.TransformSpellId = holder.Spell.Id;
        target.TransformHolder = holder;
        target.TransformScale = display.Scale;
        target.DisplayId = display.DisplayId;
        // SetTransformScale replaces the visual override; native scale is
        // retained only for restoration, independently of ModScale auras.
        target.SetFloat(UpdateFields.ObjectFieldScaleX, display.Scale * ActiveScaleFactor(system, target));
        system.UpdateDisplayModel(target);
    }

    internal static void RestoreFormVisual(SpellSystem system, Unit target)
    {
        if (target.TransformSpellId != 0)
        {
            return;
        }

        global::ArcaneCore.Game.Spells.ShapeshiftService.ApplyFormVisual(system, target);
    }

    private static SpellAuraHolder? Current(SpellSystem system, Unit target)
        => target.TransformHolder is { IsRemoved: false } holder && system.GetAuras(target).Contains(holder) ? holder : null;

    private static float ActiveScaleFactor(SpellSystem system, Unit target)
    {
        return VisualAuras.ActiveScaleFactor(system, target);
    }
}
