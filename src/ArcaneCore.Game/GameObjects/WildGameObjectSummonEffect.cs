using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.GameObjects;

/// <summary>Spell effect 76 places a temporary wild game object at the cast destination or caster.</summary>
public sealed class WildGameObjectSummonEffect : ISpellHandlerModule
{
    public void Register(SpellSystem system) => system.RegisterEffect(SpellEffectName.SummonObjectWild, Apply);

    private static void Apply(SpellEffectContext context)
    {
        if (context.Effect.MiscValue <= 0 || context.Caster.Map?.FindUpdater<GameObjectMapSystem>() is not { } objects)
            return;

        (float x, float y, float z) = context.Cast.Targets.HasDest
            ? context.Cast.Targets.Dest : (context.Caster.X, context.Caster.Y, context.Caster.Z);
        int durationMs = context.Spell.GetDuration();
        uint seconds = durationMs > 0 ? (uint)Math.Max(1, ((long)durationMs + 999) / 1000) : 0;
        objects.Summon((uint)context.Effect.MiscValue, x, y, z, context.Caster.Orientation, seconds);
    }
}
