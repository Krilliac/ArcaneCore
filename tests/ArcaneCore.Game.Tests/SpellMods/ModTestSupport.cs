using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.SpellMods;

/// <summary>Synthetic talent-shaped spells shared by the spell-modifier tests (never retail data).</summary>
internal static class ModTestSupport
{
    /// <summary>The spell family every synthetic spell and modifier below carries.</summary>
    public const uint Family = 3;

    /// <summary>A permanent passive with one modifier aura (107 flat or 108 pct) on <paramref name="mask"/>.</summary>
    public static SpellInfo ModPassive(uint id, AuraType aura, SpellModOp op, int value, ulong mask = 1, uint family = Family, uint procCharges = 0) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, value, aura: aura, misc: (int)op) with { ItemType = (uint)mask }) with
        {
            Attributes = SpellAttributes.Passive,
            Duration = new SpellDuration(-1, 0, -1),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
            SpellFamilyName = family,
            SpellFamilyFlags = mask,
            ProcCharges = procCharges,
        };

    public static SpellInfo Flat(uint id, SpellModOp op, int value, ulong mask = 1) => ModPassive(id, AuraType.AddFlatModifier, op, value, mask);

    public static SpellInfo Pct(uint id, SpellModOp op, int value, ulong mask = 1) => ModPassive(id, AuraType.AddPctModifier, op, value, mask);

    /// <summary>A modified spell: the family and class mask the talent spells above are keyed to.</summary>
    public static SpellInfo InFamily(SpellInfo spell, ulong flags = 1) => spell with { SpellFamilyName = Family, SpellFamilyFlags = flags };

    /// <summary>A player with the rage the spells here cost.</summary>
    public static (Player Caster, FakeSession Session) CasterWithRage(SpellTestKit kit, uint guid = 1, uint rage = 100)
    {
        (Player caster, FakeSession session) = kit.AddPlayer(guid);
        SpellSystem.SetPower(caster, PowerType.Rage, rage);
        kit.World.RunTick(0);
        session.Clear();
        return (caster, session);
    }
}
