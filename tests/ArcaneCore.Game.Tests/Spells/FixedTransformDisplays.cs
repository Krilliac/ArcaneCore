using ArcaneCore.Game.Spells;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// A Transform aura display source for tests: <paramref name="find"/> maps a creature entry to its display (null: unknown entry), every display
/// with the transform scale <paramref name="scale"/>.
/// </summary>
internal sealed class FixedTransformDisplays(Func<uint, uint?> find, float scale = 1f) : ITransformDisplaySource
{
    public uint? FindDisplay(uint creatureEntry) => find(creatureEntry);

    public TransformDisplay? FindTransform(uint creatureEntry) => find(creatureEntry) is { } display ? new TransformDisplay(display, scale) : null;

    public void ReportNoModel(uint spellId)
    {
    }

    /// <summary>Register a source on <paramref name="kit"/>'s world that gives every creature entry <paramref name="display"/> at <paramref name="scale"/>.</summary>
    public static void Use(SpellTestKit kit, uint display, float scale = 1f) => Use(kit, _ => display, scale);

    /// <summary>Register a source on <paramref name="kit"/>'s world.</summary>
    public static void Use(SpellTestKit kit, Func<uint, uint?> find, float scale = 1f)
        => TransformDisplays.Register(kit.World, new FixedTransformDisplays(find, scale));
}
