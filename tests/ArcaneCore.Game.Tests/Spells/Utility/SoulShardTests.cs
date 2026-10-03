using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Warlock;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// Soul Shards from Drain Soul and Shadowburn: vmangos Aura::HandleChannelDeathItem (SpellAuras.cpp:2833-2880): the item is made only when
/// the aura ends because its target died, once per warlock and target, from a non-grey tapped target; Player::IsHonorOrXPTarget
/// (Player.cpp:19943-19957).
/// </summary>
public sealed class SoulShardTests
{
    private const uint DrainSoul = 962_001;
    private const uint Shadowburn = 962_002;
    private const uint PlainDebuff = 962_003;
    private const uint ShardEntry = SoulShardRules.SoulShard;

    private static readonly ItemTemplateStore Items = new(
        [.. Templates, new ItemTemplate { Entry = ShardEntry, Class = 12, Name = "Soul Shard", DisplayId = 1, Stackable = 20 }], []);

    private static SpellInfo ShardAura(uint id) => Spell(id, Effect(SpellEffectName.ApplyAura, 1, SpellImplicitTarget.UnitEnemy, AuraType.ChannelDeathItem) with { ItemType = ShardEntry })
        with
        {
            SpellFamilyName = SoulShardRules.WarlockFamily,
            Duration = new SpellDuration(15_000, 0, 15_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static PetTestKit Kit() => new(
    [
        ShardAura(DrainSoul),
        ShardAura(Shadowburn),
        Spell(PlainDebuff, Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, AuraType.Dummy)) with
        {
            Duration = new SpellDuration(15_000, 0, 15_000),
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            SpellVisual = 1,
            Attributes = SpellAttributes.AuraIsDebuff,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        },
    ]);

    private static (Player Warlock, FakeSession Session) Warlock(PetTestKit kit, uint guid = 1, byte level = 5)
    {
        (Player player, FakeSession session) = kit.AddPlayer(guid);
        player.Level = level;
        player.Inventory.Templates = Items;
        player.Inventory.GuidAllocator = new ItemGuidAllocator();
        player.Inventory.Load([]);
        session.Clear();
        return (player, session);
    }

    /// <summary>A level-5 creature (the pet kit's wild entry).</summary>
    private static int _summoners;

    private static Creature Victim(PetTestKit kit)
    {
        (Player summoner, _) = kit.AddPlayer((uint)(900 + _summoners++));
        int before = kit.Creatures.Creatures.Count();
        kit.Cast(summoner, WildSpell);
        return kit.Creatures.Creatures.Skip(before).First();
    }

    private static void Drain(PetTestKit kit, Player caster, Unit victim, uint spell = DrainSoul)
        => Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(victim.Guid), triggered: true));

    private static void Die(PetTestKit kit, Unit victim)
    {
        victim.Health = 0;
        kit.Spells.System.OnUnitDied(victim);
    }

    private static uint Shards(Player player) => player.Inventory.GetItemCount(ShardEntry);

    [Fact]
    public void TargetDyingDuringTheChannel_GivesOneSoulShard_AndTheItemPush()
    {
        using PetTestKit kit = Kit();
        (Player warlock, FakeSession session) = Warlock(kit);
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim);

        Die(kit, victim);

        Assert.Equal(1u, Shards(warlock));
        Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgItemPushResult);
    }

    [Fact]
    public void ChannelEndingWithTheTargetAlive_GivesNothing()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit);
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim);
        Assert.True(kit.Spells.System.HasAura(victim, DrainSoul));

        kit.Spells.Advance(16_000);

        Assert.False(kit.Spells.System.HasAura(victim, DrainSoul));
        Assert.Equal(0u, Shards(warlock));
    }

    [Fact]
    public void AGreyTarget_GivesNothing()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit, level: 60); // gray level 47: the level 5 victim is grey
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim);

        Die(kit, victim);

        Assert.Equal(0u, Shards(warlock));
    }

    [Fact]
    public void ShadowburnAndDrainSoulOnOneTarget_GiveExactlyOneShard()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit);
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim, Shadowburn);
        Drain(kit, warlock, victim, DrainSoul);

        Die(kit, victim);

        Assert.Equal(1u, Shards(warlock));
    }

    [Fact]
    public void TwoWarlocksDraining_EachGetOneShard()
    {
        using PetTestKit kit = Kit();
        (Player first, _) = Warlock(kit, 1);
        (Player second, _) = Warlock(kit, 2);
        Creature victim = Victim(kit);
        Drain(kit, first, victim);
        Drain(kit, second, victim);

        Die(kit, victim);

        Assert.Equal((1u, 1u), (Shards(first), Shards(second)));
    }

    [Fact]
    public void APetTarget_GivesNothing()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit);
        (Player owner, _) = kit.AddPlayer(5001);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Drain(kit, warlock, pet);

        Die(kit, pet);

        Assert.Equal(0u, Shards(warlock));
    }

    [Fact]
    public void FullBags_SendTheEquipError_AndCreateNothing()
    {
        using PetTestKit kit = Kit();
        (Player warlock, FakeSession session) = Warlock(kit);
        for (int i = 0; i < 16; i++)
        {
            Give(warlock.Inventory, RecruitsShirt);
        }

        session.Clear();
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim);

        Die(kit, victim);

        Assert.Equal(0u, Shards(warlock));
        Assert.Equal([InventoryResult.InventoryFull], EquipErrors(session));
    }

    [Fact]
    public void AnUntappedKill_GivesNothing_WhenATapSourceSaysSo()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit);
        var tap = new FakeTapSource();
        SoulShardRules.UseTapSource(kit.Spells.System, tap);
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim);

        Die(kit, victim);
        Assert.Equal(0u, Shards(warlock));

        Creature tapped = Victim(kit);
        tap.Tapper = warlock.Guid;
        Drain(kit, warlock, tapped);
        Die(kit, tapped);
        Assert.Equal(1u, Shards(warlock));
    }

    [Fact]
    public void ACreatureCaster_NeverMakesAnItem()
    {
        using PetTestKit kit = Kit();
        Creature victim = Victim(kit);
        (Player owner, _) = kit.AddPlayer(5002);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(pet, DrainSoul, SpellCastTargets.ForUnit(victim.Guid), triggered: true));

        Die(kit, victim); // no exception, no item (vmangos: the caster must be a player)

        Assert.Equal(0u, Shards(owner));
    }

    [Fact]
    public void AnAuraOfADifferentType_NeverMakesAnItem()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = Warlock(kit);
        Creature victim = Victim(kit);
        Drain(kit, warlock, victim, PlainDebuff);

        Die(kit, victim);

        Assert.Equal(0u, Shards(warlock));
    }

    private sealed class FakeTapSource : ISoulShardTapSource
    {
        public ObjectGuid Tapper { get; set; }

        public bool IsTappedBy(Creature victim, Player player) => player.Guid == Tapper;
    }
}
