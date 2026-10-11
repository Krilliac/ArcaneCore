using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureAi.CreatureAiTestSupport;
using static ArcaneCore.Game.Tests.CreatureTestSupport;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

public sealed class TrainerAuraGapTests
{
    [Fact]
    public void DetectMagic_MarksTheTargetAurasVisible_OnlyWhileApplied()
    {
        using var kit = new SpellTestKit(Spell(2855, Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.AurasVisible)) with
        {
            SpellVisual = 1,
            Duration = new SpellDuration(30_000, 0, 30_000),
        });
        (Player target, _) = kit.AddPlayer(1);

        Assert.False((target.UnitFlags & UnitFlags.AurasVisible) != 0);
        Assert.True(kit.System.AddAura(target, 2855, false));
        Assert.True((target.UnitFlags & UnitFlags.AurasVisible) != 0);
        kit.System.RemoveAuras(target, 2855);
        Assert.False((target.UnitFlags & UnitFlags.AurasVisible) != 0);
    }

    [Fact]
    public void SootheAnimal_ReducesCreatureAggroRadius_AndRemovalRestoresIt()
    {
        // vmangos Creature::GetAttackDistance (Creature.cpp:2225-2237): the creature's aura amount
        // joins the radius before the floor, if its spell can affect the creature's level.
        using var kit = new SpellTestKit(Spell(2908, Effect(SpellEffectName.ApplyAura, -10, aura: AuraType.ModDetectRange)) with
        {
            SpellVisual = 1,
            MaxTargetLevel = 10,
            Duration = new SpellDuration(30_000, 0, 30_000),
        });
        var content = Content([Template()], [Spawn(1, WolfEntry, 5, 0)]);
        var queries = new SpellSystemUnitSpellQueries(kit.System);
        (_, _, CreatureMapSystem creatures) = CreateAiSystem(content, new CreatureAiServices { UnitSpells = queries }, world: kit.World);
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 2;
        Creature wolf = Assert.Single(creatures.Creatures);

        Assert.Equal(18f, creatures.GetAttackDistance(wolf, player));
        Assert.True(kit.System.AddAura(wolf, 2908, false));
        Assert.Equal(8f, creatures.GetAttackDistance(wolf, player));
        kit.System.RemoveAuras(wolf, 2908);
        Assert.Equal(18f, creatures.GetAttackDistance(wolf, player));

        // The aura still applies to a level-11 creature, but its MaxTargetLevel is 10:
        // vmangos excludes its amount from this creature's detection range.
        wolf.Level = 11;
        player.Level = 11;
        Assert.True(kit.System.AddAura(wolf, 2908, false));
        Assert.Equal(18f, creatures.GetAttackDistance(wolf, player));
        kit.System.RemoveAuras(wolf, 2908);
        Assert.Equal(18f, creatures.GetAttackDistance(wolf, player));
    }
}
