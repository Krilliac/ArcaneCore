using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Protocol;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;

namespace ArcaneCore.Game.Tests.Death;

public sealed class SelfResurrectionTests
{
    private const uint SpellId = 990094;

    private static SpellInfo Spell(int value, int mana = 0) => SpellTestKit.Spell(SpellId,
        new SpellEffectInfo { Effect = SpellEffectName.SelfResurrect, BasePoints = value, BaseDice = 0,
            DieSides = 1, MiscValue = mana, TargetA = SpellImplicitTarget.UnitCaster }) with
    {
        Attributes = SpellAttributes.AllowCastWhileDead,
        AttributesEx2 = SpellAttributesEx2.AllowDeadTarget,
        StartRecoveryCategory = 0, StartRecoveryTime = 0,
    };

    [Theory]
    [InlineData(25, 999, 250u, 200u)]
    [InlineData(-300, 77, 300u, 77u)]
    [InlineData(200, 0, 1000u, 800u)]
    [InlineData(int.MinValue, int.MaxValue, 1000u, 800u)]
    [InlineData(-300, -77, 300u, 0u)]
    public void NormalCastRestoresSourceVitals_AndClearsBodyGhostRootAndOffer(int value, int mana, uint health, uint expectedMana)
    {
        using var kit = new SpellTestKit(Spell(value, mana));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 800);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Energy, 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage, 80);
        player.Map!.Combat.KillPlayer(player);
        Assert.True(player.Map!.Combat.RepopPlayer(player));
        var offer = new ResurrectionRequest(new ObjectGuid(2), new(0, player.X, player.Y, player.Z, 0), 0, 100, 100, false);
        Assert.True(PlayerResurrection.TryOffer(player, offer));
        kit.Spellbook.Teach(player, SpellId);

        Assert.Equal(SpellCastResult.CastOk, kit.System.HandleCastRequest(player, SpellId, SpellCastTargets.ForSelf()));

        Assert.True(player.IsAlive);
        Assert.Equal(health, player.Health);
        Assert.Equal(expectedMana, player.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(0u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));
        Assert.Equal(100u, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Energy));
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
        Assert.False(player.IsRooted);
        Assert.Null(player.Combat.Corpse);
        Assert.Empty(player.Map!.Combat.Corpses);
        Assert.Null(PlayerResurrection.GetRequest(player));
    }

    [Fact]
    public void GhostInAnotherMap_RemovesBodyFromItsOwningMap_WithoutRelocation()
    {
        using var kit = new SpellTestKit(Spell(25));
        (Player player, _) = kit.AddPlayer(1);
        var bodyMap = player.Map!;
        bodyMap.Combat.KillPlayer(player);
        Assert.True(bodyMap.Combat.RepopPlayer(player));
        bodyMap.RemovePlayer(player);
        player.MapId = 1;
        player.Relocate(900, 800, player.Z, 1, 0);
        kit.World.GetMap(1).AddPlayer(player);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true));

        Assert.True(player.IsAlive);
        Assert.Equal((1u, 900f, 800f, 1f), (player.MapId, player.X, player.Y, player.Orientation));
        Assert.Null(player.Combat.Corpse);
        Assert.Empty(bodyMap.Combat.Corpses);
    }

    [Fact]
    public void AliveOrOfflinePlayerIsNotRestored_AndSettlementBlocksTheCast()
    {
        using var kit = new SpellTestKit(Spell(25));
        (Player player, _) = kit.AddPlayer(1);
        player.Health = 5;
        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true));
        Assert.Equal(5u, player.Health);
        player.Map!.Combat.KillPlayer(player);
        Guid hold = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(hold));
        Assert.Equal(SpellCastResult.NotReady, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true));
        Assert.False(player.IsAlive);
        Assert.True(player.EndQuestSettlement(hold));
        kit.World.RemovePlayer(player);
        kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true);
        Assert.False(player.IsAlive);
    }

    [Fact]
    public void DeadCasterStillRequiresAllowCastWhileDead()
    {
        using var kit = new SpellTestKit(Spell(25) with { Attributes = SpellAttributes.None });
        (Player player, _) = kit.AddPlayer(1);
        player.Map!.Combat.KillPlayer(player);
        Assert.Equal(SpellCastResult.CasterDead, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true));
        Assert.False(player.IsAlive);
    }

    [Theory]
    [InlineData(0.0, 33u, 33u)]
    [InlineData(0.999, 34u, 34u)]
    public void PercentageRoundingUsesFractionalDither(double roll, uint health, uint mana)
    {
        using var kit = new SpellTestKit(Spell(33));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 101;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 103);
        player.Map!.Combat.KillPlayer(player);
        kit.System.Random = new FixedRandom(roll);

        Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true));

        Assert.Equal(health, player.Health);
        Assert.Equal(mana, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    [Fact]
    public void ZeroPercentPreservesSourceAliveDeathState_AndReplayDoesNotRestoreAgain()
    {
        using var kit = new SpellTestKit(Spell(0));
        (Player player, FakeSession session) = kit.AddPlayer(1);
        player.Map!.Combat.KillPlayer(player);
        Assert.True(player.Map!.Combat.RepopPlayer(player));
        kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true);
        Assert.Equal(DeathState.Alive, player.Combat.DeathState);
        Assert.Equal(0u, player.Health);
        Assert.Null(player.Combat.Corpse);
        Assert.False(player.Flags.HasFlag(PlayerFlags.Ghost));
        session.Clear();

        kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true);

        Assert.Equal(0u, player.Health);
        Assert.DoesNotContain(session.Sent, p => p.Opcode == WorldOpcode.SmsgMoveLandWalk);
    }

    [Fact]
    public void DeadCreatureIsNotResurrected()
    {
        using var kit = new SpellTestKit(Spell(25));
        var template = new CreatureTemplate { Entry = 991094, Name = "Creature", MinLevel = 1, MaxLevel = 1,
            Faction = 35, MinLevelHealth = 100, MaxLevelHealth = 100 };
        var creature = new Creature(1, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
        kit.World.GetMap(0).AddObject(creature, true);
        creature.Health = 0;
        creature.Combat.DeathState = DeathState.Corpse;

        kit.System.CastSpell(creature, SpellId, SpellCastTargets.ForSelf(), true);

        Assert.False(creature.IsAlive);
        Assert.Equal(DeathState.Corpse, creature.Combat.DeathState);
    }

    [Fact]
    public void ZeroHealthCompletedRevival_CannotReceiveAnotherResurrectionOffer()
    {
        using var kit = new SpellTestKit(Spell(0), ResurrectionSpellTests.Resurrection(990018, SpellEffectName.Resurrect, 25));
        (Player player, _) = kit.AddPlayer(1);
        (Player caster, _) = kit.AddPlayer(2);
        player.Map!.Combat.KillPlayer(player);
        kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true);
        Assert.Equal(DeathState.Alive, player.Combat.DeathState);

        kit.System.CastSpell(caster, 990018, SpellCastTargets.ForUnit(player.Guid), true);

        Assert.Null(PlayerResurrection.GetRequest(player));
        Assert.False(PlayerResurrection.TryOffer(player, new ResurrectionRequest(caster.Guid,
            new(0, player.X, player.Y, player.Z, 0), 0, 100, 100, false)));
    }

    [Fact]
    public void ZeroHealthAliveState_CannotAcceptAnExistingOffer()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        player.Map!.Combat.KillPlayer(player);
        var caster = new ObjectGuid(2);
        Assert.True(PlayerResurrection.TryOffer(player, new ResurrectionRequest(caster,
            new(0, player.X, player.Y, player.Z, 0), 0, 100, 100, false)));
        player.Combat.DeathState = DeathState.Alive;
        player.Health = 0;

        Assert.Null(PlayerResurrection.TryAccept(player, caster));
    }

    [Fact]
    public void RemovingGhostStateRecomputesMaxima_BeforeRestoredVitalsAreClamped()
    {
        using var kit = new SpellTestKit(Spell(25));
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 800);
        player.Map!.Combat.KillPlayer(player);
        player.Map!.Combat.Hooks = new LowerMaximaOnRevive();

        kit.System.CastSpell(player, SpellId, SpellCastTargets.ForSelf(), true);

        Assert.Equal(150u, player.Health);
        Assert.Equal(50u, player.GetUInt32(UpdateFields.UnitFieldPower1));
    }

    private sealed class LowerMaximaOnRevive : CombatHooks
    {
        public override void OnResurrected(Player player, bool applySickness)
        {
            player.MaxHealth = 150;
            player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 50);
        }
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}
