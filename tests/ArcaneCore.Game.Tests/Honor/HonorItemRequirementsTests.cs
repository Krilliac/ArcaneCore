using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Skills;
using ArcaneCore.Game.Stats;
using ArcaneCore.Kernel.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>Equip requirements use the HIGHEST rank at 1.12 (vmangos Player.cpp:10078-10081).</summary>
public sealed class HonorItemRequirementsTests
{
    private static (HonorService Service, Player Player) Setup(float rankPoints, byte highest)
    {
        var service = new HonorService(new HonorOptions(), new FixedHonorClock(20_000), () => 19_997);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        service.Track(player, service.Create(player, CharacterHonorData.Empty with { State = CharacterHonorState.Empty with { RankPoints = rankPoints, HighestRank = highest } }));
        return (service, player);
    }

    [Fact]
    public void Honor_rank_is_the_highest_rank_while_everything_else_passes_through()
    {
        (HonorService service, Player player) = Setup(rankPoints: 5500f, highest: 12); // current rank 7, highest 12
        var requirements = new HonorItemRequirements(DefaultItemRequirements.Instance, service);
        Assert.Equal((byte)12, requirements.HonorRank(player.Inventory));
        Assert.False(requirements.CanDualWield(player.Inventory));
        Assert.Equal(300u, requirements.SkillValue(player.Inventory, 43));
        Assert.True(requirements.HasSpell(player.Inventory, 1));
        Assert.Equal(3u, requirements.ReputationRank(player.Inventory, 72));
    }

    [Fact]
    public void An_inventory_without_a_player_or_an_untracked_player_falls_back_to_the_wrapped_provider()
    {
        (HonorService service, _) = Setup(5500f, 12);
        var requirements = new HonorItemRequirements(DefaultItemRequirements.Instance, service);
        Player stranger = TestWorld.CreatePlayer(2, 0, 0, new FakeSession());
        Assert.Equal((byte)0, requirements.HonorRank(stranger.Inventory));
    }

    [Fact]
    public void The_wrapping_order_with_the_other_requirement_decorators_does_not_matter()
    {
        (HonorService service, Player player) = Setup(5500f, 12);
        IItemRequirements honorFirst = new PlayerItemRequirements(new HonorItemRequirements(DefaultItemRequirements.Instance, service));
        IItemRequirements honorLast = new HonorItemRequirements(new StatStateItemRequirements(new PlayerItemRequirements(DefaultItemRequirements.Instance)), service);
        Assert.Equal((byte)12, honorFirst.HonorRank(player.Inventory));
        Assert.Equal((byte)12, honorLast.HonorRank(player.Inventory));
    }

    [Fact]
    public void Install_wraps_once_and_composes_with_an_existing_provider()
    {
        (HonorService service, Player player) = Setup(5500f, 12);
        HonorItemRequirements.Install(player.Inventory, service);
        HonorItemRequirements.Install(player.Inventory, service);
        Assert.Equal((byte)12, player.Inventory.Requirements.HonorRank(player.Inventory));
        Assert.IsType<HonorItemRequirements>(player.Inventory.Requirements);
        Assert.IsNotType<HonorItemRequirements>(((HonorItemRequirements)player.Inventory.Requirements).Inner);
    }
}
