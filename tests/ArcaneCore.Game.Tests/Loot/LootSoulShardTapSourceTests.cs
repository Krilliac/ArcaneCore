using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Groups;
using ArcaneCore.Game.Loot;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Tests.GameObjects;
using ArcaneCore.Game.Tests.Pets;
using ArcaneCore.Kernel.WorldData.Loot;
using Xunit;

namespace ArcaneCore.Game.Tests.Loot;

/// <summary>
/// The shipped Soul Shard tap source (LootSoulShardTapSource) over the loot tap (Creature.LootTapPlayerGuid / LootTapGroup, Creature.cs:119-121;
/// LootService.IsTappedBy, LootService.Special.cs:78, after vmangos Creature::IsTappedBy, Creature.cpp:1615-1632), and its fallback: no recorded
/// tap is untapped unless the option is set, no loot service counts as tapped.
/// </summary>
public sealed class LootSoulShardTapSourceTests
{
    private const uint BeastEntry = 5241;

    private static PetTestKit Kit() => new(extraTemplates:
    [
        CreatureTestKit.Template(BeastEntry, b =>
        {
            b.MinLevel = 20;
            b.MaxLevel = 20;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
            b.Faction = 14;
        }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
    ]);

    private static Creature Victim(PetTestKit kit) => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);

    private static (LootSoulShardTapSource Source, FakeGroups Groups) Source()
    {
        var groups = new FakeGroups();
        var loot = new LootService(new LootContent([], [])) { Groups = groups };
        return (new LootSoulShardTapSource(_ => loot), groups);
    }

    [Fact]
    public void AnotherPlayersTap_IsNotTapped()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        (Player other, _) = kit.AddPlayer(2);
        Creature victim = Victim(kit);
        victim.LootTapPlayerGuid = other.Guid;

        Assert.False(Source().Source.IsTappedBy(victim, warlock));
    }

    [Fact]
    public void TheWarlocksOwnTap_IsTapped()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        Creature victim = Victim(kit);
        victim.LootTapPlayerGuid = warlock.Guid;

        Assert.True(Source().Source.IsTappedBy(victim, warlock));
    }

    [Fact]
    public void AGroupMatesTap_IsTapped()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        (Player friend, _) = kit.AddPlayer(2);
        Creature victim = Victim(kit);
        (LootSoulShardTapSource source, FakeGroups groups) = Source();
        Group group = groups.Create(LootMethod.FreeForAll, friend, warlock);
        victim.LootTapPlayerGuid = friend.Guid;
        victim.LootTapGroup = group;

        Assert.True(source.IsTappedBy(victim, warlock));
    }

    [Fact]
    public void AnUntappedCreature_IsNotTappedByDefault()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);

        Assert.False(Source().Source.IsTappedBy(Victim(kit), warlock));
    }

    [Fact]
    public void AnUntappedCreature_CountsAsTappedWithTheOption()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        var loot = new LootService(new LootContent([], [])) { Groups = new FakeGroups() };

        Assert.True(new LootSoulShardTapSource(_ => loot, untappedCountsAsTapped: true).IsTappedBy(Victim(kit), warlock));
    }

    [Fact]
    public void NoLootService_CountsAsTapped()
    {
        using PetTestKit kit = Kit();
        (Player warlock, _) = kit.AddPlayer(1);
        (Player other, _) = kit.AddPlayer(2);
        Creature victim = Victim(kit);
        victim.LootTapPlayerGuid = other.Guid;

        Assert.True(new LootSoulShardTapSource(_ => null).IsTappedBy(victim, warlock));
    }
}
