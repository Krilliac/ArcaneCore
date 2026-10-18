using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Generic;
using ArcaneCore.Game.Spells.Scripts;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>vmangos Spell::EffectDummy / EffectScriptEffect cases ported to <c>Spells/Generic/WorldEventDummyScripts.cs</c>.</summary>
public sealed class WorldEventDummyScriptTests
{
    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = 1 };

    private static SpellInfo Self(uint id, SpellEffectName effect = SpellEffectName.ScriptEffect) => Instant(Spell(id, Effect(effect, 0, SpellImplicitTarget.UnitCaster)));

    private static SpellInfo Marker(uint id) => Instant(Spell(id, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitCaster, AuraType.Dummy)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
    });

    private static SpellTestKit Kit(params SpellInfo[] spells)
    {
        var kit = new SpellTestKit(spells);
        SpellScriptDispatcher.Install(kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        return kit;
    }

    [Fact]
    public void Valentine_GiftTablesMatchVmangos()
    {
        Assert.Equal((27242u, 27510u), ValentineAnswerScript.GiftsFor(26923, 11));
        Assert.Equal((27519u, 26901u), ValentineAnswerScript.GiftsFor(27541, 80));
        Assert.Null(ValentineAnswerScript.GiftsFor(26923, 12));
        Assert.Null(ValentineAnswerScript.GiftsFor(26663, 1));
    }

    [Fact]
    public void SilithusFlagClick_GivesTheSilithystAura()
    {
        using SpellTestKit kit = Kit(Self(29518, SpellEffectName.Dummy), Marker(29519));
        (Player clicker, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(clicker, 29518, SpellCastTargets.ForSelf(), triggered: true));
        Assert.True(kit.System.HasAura(clicker, 29519));
    }

    [Fact]
    public void Scripts_AreRegistered()
    {
        var registry = SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly);
        foreach (uint id in new uint[] { 25680, 19395, 23019, 28697, 23845, 14813, 29518, 27686, 26923, 26663, 27550, 27654, 26870 })
        {
            Assert.True(registry.Find(id) is not null, $"spell {id} has no script");
        }
    }
}
