using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Death.Resurrection;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.Death;

/// <summary>
/// Soulstone, Twisting Nether and Reincarnation (vmangos Player::SelectResurrectionSpellId, Player.cpp:19868-19945, SetDeathState
/// 1507-1570, HandleSelfResOpcode SpellHandler.cpp:461-485, EffectSelfResurrect SpellEffects.cpp:5334-5368). Spell shapes are the
/// classic-db rows: 3026 is effect 94 with -401 base points and misc 700; 21169 is 19 base points (20%).
/// </summary>
public sealed class SelfResurrectionTests
{
    private const long T = 1_700_000_000;

    private static readonly SpellInfo SoulstoneBuff = SpellTestKit.Spell(20707, SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        SpellVisual = 99,
        SpellIconId = 92,
        Duration = new SpellDuration(1_800_000, 0, 1_800_000),
    };

    private static readonly SpellInfo SoulstoneEffect = SpellTestKit.Spell(3026, SpellTestKit.Effect(SpellEffectName.SelfResurrect, -400, misc: 700)) with
    {
        Attributes = (SpellAttributes)0x00800000, // castable while dead
    };

    private static readonly SpellInfo TwistingNetherBuff = SpellTestKit.Spell(SelfResurrection.TwistingNetherPassive,
        SpellTestKit.Effect(SpellEffectName.ApplyAura, 0, aura: AuraType.Dummy)) with
    {
        Attributes = SpellAttributes.Passive,
        Duration = new SpellDuration(-1, 0, -1),
    };

    private static readonly SpellInfo ReincarnationEffect = SpellTestKit.Spell(SelfResurrection.ReincarnationEffect, SpellTestKit.Effect(SpellEffectName.SelfResurrect, 20)) with
    {
        Attributes = (SpellAttributes)0x00800000,
        RecoveryTime = 3_600_000,
        StartRecoveryTime = 0,
        StartRecoveryCategory = 0,
    };

    private static readonly ItemTemplateStore Items = new(
        [.. Templates, new ItemTemplate { Entry = SelfResurrection.Ankh, Class = 5, Name = "Ankh", DisplayId = 1, Stackable = 20 }], []);

    private sealed class ForcedRandom(int value) : Random
    {
        public override int Next(int maxValue) => Math.Min(value, maxValue - 1);
    }

    private sealed class Rig : IDisposable
    {
        public Rig(int roll = 99)
        {
            Kit = new SpellTestKit(SoulstoneBuff, SoulstoneEffect, TwistingNetherBuff, ReincarnationEffect);
            Kit.System.Random = new ForcedRandom(roll);
            DeathHooks.Register(Kit.World, new DeathHooks(new DeathOptions(), new FixedDeathClock(T)));
            Session = new FakeSession(5);
            Player = CombatTestKit.AddPlayer(Kit.World, 5, 10, 10, Session);
            Wire(Player.Inventory);
            Player.Inventory.Templates = Items;
            Player.Inventory.Load([]);
            Player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 1000); // a mana pool to refill
            Kit.World.RunTick(1);
            Session.Clear();
        }

        public SpellTestKit Kit { get; }

        public FakeSession Session { get; }

        public Player Player { get; }

        public MapCombat Combat => Kit.World.GetMap(0).Combat;

        public uint SelfResSpell => Player.GetUInt32(UpdateFields.PlayerSelfResSpell);

        public void Buff(uint spell) => Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Player, spell, SpellCastTargets.ForSelf(), triggered: true));

        /// <summary>Die as the daemon does: combat kills, then the spell system's death hook runs.</summary>
        public void Die()
        {
            Combat.Kill(null, Player);
            Kit.System.OnUnitDied(Player);
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void ASoulstoneBuff_AtDeath_SelectsItsEffectSpell_EvenThoughTheDeathStripsTheBuff()
    {
        using var rig = new Rig();
        rig.Buff(20707);
        Assert.True(rig.Kit.System.HasAura(rig.Player, 20707));

        rig.Die();

        Assert.Equal(3026u, rig.SelfResSpell);
        Assert.False(rig.Kit.System.HasAura(rig.Player, 20707)); // not death persistent: gone, the field was chosen first
    }

    [Fact]
    public void UsingIt_ResurrectsWithTheFlatHealthAndMana_EmptiesTheField_AndTheCorpseGoes()
    {
        using var rig = new Rig();
        rig.Buff(20707);
        rig.Die();
        Assert.True(rig.Combat.RepopPlayer(rig.Player));
        Assert.NotNull(rig.Player.Combat.Corpse);

        Assert.True(SelfResurrection.Use(rig.Kit.System, rig.Player));

        Assert.True(rig.Player.IsAlive);
        Assert.Equal(400u, rig.Player.Health); // -(-400)
        Assert.Equal(700u, rig.Player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Mana));
        Assert.Equal(0u, rig.SelfResSpell);
        Assert.Null(rig.Player.Combat.Corpse);
        Assert.Equal(0u, (uint)(rig.Player.Flags & PlayerFlags.Ghost));
    }

    [Fact]
    public void WithNothingToUse_NothingHappens()
    {
        using var rig = new Rig();
        rig.Die();

        Assert.Equal(0u, rig.SelfResSpell);
        Assert.False(SelfResurrection.Use(rig.Kit.System, rig.Player));
        Assert.False(rig.Player.IsAlive);
    }

    [Fact]
    public void AnyOtherResurrection_EmptiesTheField()
    {
        using var rig = new Rig();
        rig.Buff(20707);
        rig.Die();
        Assert.Equal(3026u, rig.SelfResSpell);

        rig.Combat.ResurrectPlayer(rig.Player, 0.5f, applySickness: false);

        Assert.Equal(0u, rig.SelfResSpell);
    }

    [Fact]
    public void ATwistingNether_WorksOnATenPercentRoll()
    {
        using var works = new Rig(roll: 5);
        works.Buff(SelfResurrection.TwistingNetherPassive);
        works.Die();
        Assert.Equal(SelfResurrection.TwistingNetherEffect, works.SelfResSpell);

        using var fails = new Rig(roll: 10);
        fails.Buff(SelfResurrection.TwistingNetherPassive);
        fails.Die();
        Assert.Equal(0u, fails.SelfResSpell);
    }

    [Fact]
    public void ASoulstoneThenTwistingNether_ThatRolls_TakesTheTwistingNether_AsVmangosLoopDoes()
    {
        using var rig = new Rig(roll: 5);
        rig.Buff(20707);
        rig.Buff(SelfResurrection.TwistingNetherPassive);
        rig.Die();

        Assert.Equal(SelfResurrection.TwistingNetherEffect, rig.SelfResSpell);
    }

    [Fact]
    public void TwistingNetherThenASoulstone_KeepsTheTwistingNether_BecauseThePriorityGuardRefusesTheSoulstone()
    {
        using var rig = new Rig(roll: 5);
        rig.Buff(SelfResurrection.TwistingNetherPassive);
        rig.Buff(20707);
        rig.Die();

        Assert.Equal(SelfResurrection.TwistingNetherEffect, rig.SelfResSpell);
    }

    [Fact]
    public void ASoulstoneAlone_WinsWhenTheTwistingNetherRollFails()
    {
        using var rig = new Rig(roll: 50);
        rig.Buff(20707);
        rig.Buff(SelfResurrection.TwistingNetherPassive);
        rig.Die();

        Assert.Equal(3026u, rig.SelfResSpell);
    }

    [Fact]
    public void Reincarnation_NeedsThePassive_AnAnkh_AndAReadyEffectSpell()
    {
        using var rig = new Rig();
        Give(rig.Player.Inventory, SelfResurrection.Ankh, 2);

        // without the passive spell: nothing
        rig.Die();
        Assert.Equal(0u, rig.SelfResSpell);
        rig.Combat.ResurrectPlayer(rig.Player, 1f, applySickness: false);

        rig.Kit.Spellbook.LearnSpell(rig.Player, SelfResurrection.ReincarnationPassive);
        rig.Die();
        Assert.Equal(SelfResurrection.ReincarnationEffect, rig.SelfResSpell);

        // used: 20% of the maximum, an Ankh gone, and the hour of cooldown running
        Assert.True(SelfResurrection.Use(rig.Kit.System, rig.Player));
        Assert.Equal(200u, rig.Player.Health);
        Assert.Equal(1u, rig.Player.Inventory.GetItemCount(SelfResurrection.Ankh));
        rig.Die();
        Assert.Equal(0u, rig.SelfResSpell); // an Ankh is left, the spell is not ready

        rig.Combat.ResurrectPlayer(rig.Player, 1f, applySickness: false);
        rig.Kit.Advance(3_601_000, step: 100_000);
        rig.Die();
        Assert.Equal(SelfResurrection.ReincarnationEffect, rig.SelfResSpell);
    }

    [Fact]
    public void WithoutAnAnkh_ReincarnationIsNotOffered()
    {
        using var rig = new Rig();
        rig.Kit.Spellbook.LearnSpell(rig.Player, SelfResurrection.ReincarnationPassive);

        rig.Die();

        Assert.Equal(0u, rig.SelfResSpell);
    }

    [Fact]
    public void ASelfResurrectionSpellThatWasAlreadyChosen_IsKept()
    {
        using var rig = new Rig();
        rig.Player.SetUInt32(UpdateFields.PlayerSelfResSpell, 4242);
        rig.Buff(20707);

        rig.Die();

        Assert.Equal(4242u, rig.SelfResSpell);
    }
}
