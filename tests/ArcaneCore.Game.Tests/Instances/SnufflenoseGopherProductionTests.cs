using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.GameObjects;
using ArcaneCore.Game.Instances.Scripts.Classic;
using ArcaneCore.Game.Instances.Scripts.RazorfenKraul;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.GameObjects;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Instances;

/// <summary>
/// The Snufflenose Gopher the way a player gets it: spell 6918 (ClassicDB z2815: SUMMON_GUARDIAN of 4781) through the real spell system and
/// summon service, then the Snufflenose Command (8283: DUMMY at TARGET_SCRIPT) cast by its owner.
/// </summary>
public sealed class SnufflenoseGopherProductionTests
{
    private const uint SummonSnufflenose = 6918;
    private const uint ScriptTarget = 38; // TARGET_SCRIPT

    [Fact]
    public void SummonedGopher_RunsItsScript_FollowsItsOwner_AndDigsUpATubberOnCommand()
    {
        using var kit = new PetTestKit(
            [
                Spell(SummonSnufflenose, Effect(SpellEffectName.SummonGuardian, 1, misc: (int)SnufflenoseGopherAi.Entry)),
                Spell(SnufflenoseGopherAi.CommandSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)ScriptTarget)) with
                {
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                },
            ],
            extraTemplates: [CreatureTestSupport.Template(SnufflenoseGopherAi.Entry, b => b.Faction = 35)]);
        kit.Map.AddUpdater(new RazorfenKraulInstance(kit.Map));
        var objects = new GameObjectMapSystem(kit.Map, new GameObjectContent(
            [GameObjectTestKit.GoTemplate(SnufflenoseGopherAi.BlueleafTubber, GameObjectType.Chest, (3, 1u))],
            [GameObjectTestKit.GoSpawn(1, SnufflenoseGopherAi.BlueleafTubber, 12, 0)], [], [], []));
        kit.Map.AddUpdater(objects);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SnufflenoseCommandSpell).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        kit.Run(200);
        GameObject tubber = Assert.Single(objects.GameObjects, go => go.Entry == SnufflenoseGopherAi.BlueleafTubber);
        tubber.Flags |= GameObjectFlags.InteractCond;
        Assert.True(objects.DespawnForRespawn(tubber));
        kit.Run(100);
        Assert.False(tubber.IsSpawned);

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, SummonSnufflenose));

        Creature gopher = Assert.Single(kit.Creatures.Creatures, c => c.Template.Entry == SnufflenoseGopherAi.Entry);
        Assert.Equal(SummonKind.Guardian, gopher.Summon?.Kind);
        var ai = Assert.IsType<SnufflenoseGopherAi>(gopher.AI);
        Assert.Same(ai.Pet, PetAI.Of(gopher.AI)); // the pet systems still reach its PetAI half (owner attacked, commands)
        kit.Run(500);
        Assert.True(gopher.GetCharmInfo() is { } following && (following.IsFollowing || following.IsReturning));

        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, SnufflenoseGopherAi.CommandSpell));

        Assert.True(ai.IsMovingToTubber);
        Assert.Equal(tubber.Guid, ai.TargetTubber);
        kit.Run(10_000);
        Assert.False(ai.IsMovingToTubber);
        Assert.Equal(GameObjectFlags.None, tubber.Flags & GameObjectFlags.InteractCond);
        Assert.True(gopher.GetCharmInfo() is { } back && (back.IsFollowing || back.IsReturning)); // ScriptedPetAI goes back to its owner
    }

    [Fact]
    public void Command_WithNoGopherInRange_DoesNothing()
    {
        using var kit = new PetTestKit(
            [
                Spell(SnufflenoseGopherAi.CommandSpell, Effect(SpellEffectName.Dummy, 1, (SpellImplicitTarget)ScriptTarget)) with
                {
                    RangeIndex = 4,
                    Range = new SpellRange(0, 30),
                },
            ]);
        SpellScriptDispatcher.Install(kit.Spells.System, SpellScriptRegistry.Discover(typeof(SnufflenoseCommandSpell).Assembly));
        (Player player, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, SnufflenoseGopherAi.CommandSpell));
    }
}
