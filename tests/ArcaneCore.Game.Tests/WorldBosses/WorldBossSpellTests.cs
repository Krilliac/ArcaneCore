using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Creatures.Scripts.WorldBosses;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.WorldBosses;

/// <summary>The spell side of the world bosses in the live spell system.</summary>
public sealed class WorldBossSpellTests
{
    [Fact]
    public void DreamFog_SleepsASingleTarget()
    {
        using var kit = new SpellTestKit(Spell(DragonsOfNightmareSpellModule.SpellDreamFogSleep,
            Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.EnumUnitsEnemyAoeAtSrcLoc, AuraType.ModStun) with { Radius = 30 }) with
        {
            Duration = new SpellDuration(10_000, 0, 10_000), StartRecoveryCategory = 0, StartRecoveryTime = 0,
        });
        var relations = new FakeRelations();
        kit.System.Relations = relations;
        (Player caster, _) = kit.AddPlayer(1);
        Player[] players = [kit.AddPlayer(2, 3).Item1, kit.AddPlayer(3, 5).Item1, kit.AddPlayer(4, 7).Item1];
        foreach (Player player in players) relations.Hostile.Add(player.Guid);
        kit.World.RunTick(0);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(caster, DragonsOfNightmareSpellModule.SpellDreamFogSleep, SpellCastTargets.ForSelf(), triggered: true));
        Assert.Equal(1, players.Count(p => kit.System.HasAura(p, DragonsOfNightmareSpellModule.SpellDreamFogSleep)));
    }

    [Fact]
    public void MaxTargetsOverride_HasOneOwner()
    {
        using var kit = new SpellTestKit();
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterSpellMaxTargetsOverride(DragonsOfNightmareSpellModule.SpellDreamFogSleep, 1)); // the module owns it
        kit.System.RegisterSpellMaxTargetsOverride(99_999, 1);
        Assert.Throws<InvalidOperationException>(() => kit.System.RegisterSpellMaxTargetsOverride(99_999, 2));
    }

    [Fact]
    public void MarkOfFrost_IsCreditedToAzuregos()
    {
        using var kit = new SpellTestKit(Spell(AzuregosAI.SpellMarkOfFrostPlayer, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
        {
            Duration = new SpellDuration(-1, 0, -1),
        });
        (Player victim, _) = kit.AddPlayer(1);
        (Player azuregos, _) = kit.AddPlayer(2, 5); // any unit stands in for the caster
        kit.World.RunTick(0);
        SpellAuraHolder? added = null;
        kit.System.HolderAdded += h => added = h;

        Assert.Equal(CreatureCastResult.Ok, new SpellSystemCreatureCaster(kit.System).AddAuraFrom(victim, AzuregosAI.SpellMarkOfFrostPlayer, azuregos));
        Assert.True(kit.System.HasAura(victim, AzuregosAI.SpellMarkOfFrostPlayer));
        Assert.Equal(azuregos.Guid, added!.CasterGuid);
    }
}
