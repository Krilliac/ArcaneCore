using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Reputation;
using ArcaneCore.Game.Skills;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Reputation;
using Xunit;
using static ArcaneCore.Game.Tests.Reputation.ReputationFixtures;

namespace ArcaneCore.Game.Tests.Reputation;

/// <summary>Item reputation gates read the player's real rank (Player.cpp:10045 CanUseItem).</summary>
public sealed class ReputationItemRequirementsTests
{
    private const uint RevereredItemEntry = 77001;
    private readonly ReputationService _service = new(Factions, roll: () => 0.0);
    private readonly Player _player;

    public ReputationItemRequirementsTests()
    {
        _player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        _service.Track(_player, _service.Create(_player, CharacterReputationData.Empty));
    }

    private static ItemTemplate Gated(ReputationRank rank, uint faction = BootyBay)
        => new() { Entry = RevereredItemEntry, Class = 4, SubClass = 1, Name = "Gated", InventoryType = 1,
            RequiredReputationFaction = faction, RequiredReputationRank = (uint)rank };

    private InventoryResult Use(ItemTemplate template)
        => _player.Inventory.CanUseItem(Item.Create(1, template, _player.Guid));

    [Fact]
    public void CanUseItem_RequiresTheRank_Neutral_Honored_Revered()
    {
        // Default seam: everybody is Neutral, so a rank 6 item can never be equipped (the bug this fixes).
        Assert.Equal(InventoryResult.CantEquipReputation, Use(Gated(ReputationRank.Revered)));

        _player.Inventory.Requirements = new ReputationItemRequirements(DefaultItemRequirements.Instance, _service);
        Assert.Equal(InventoryResult.CantEquipReputation, Use(Gated(ReputationRank.Revered)));
        Assert.Equal(InventoryResult.Ok, Use(Gated(ReputationRank.Neutral)));

        _service.ModifyReputation(_player, BootyBay, 9000); // Honored starts at 9000
        Assert.Equal(InventoryResult.CantEquipReputation, Use(Gated(ReputationRank.Revered)));
        Assert.Equal(InventoryResult.Ok, Use(Gated(ReputationRank.Honored)));

        _service.ModifyReputation(_player, BootyBay, 12000); // 21000 = Revered, no relog needed
        Assert.Equal(InventoryResult.Ok, Use(Gated(ReputationRank.Revered)));
        _service.ModifyReputation(_player, BootyBay, 100000);
        Assert.Equal(InventoryResult.Ok, Use(Gated(ReputationRank.Exalted)));
    }

    [Fact]
    public void Decorator_StacksOverSkillsRequirements_AndForwardsTheRest()
    {
        var skills = new PlayerItemRequirements(DefaultItemRequirements.Instance);
        var requirements = new ReputationItemRequirements(skills, _service);
        PlayerInventory inventory = _player.Inventory;
        Assert.Equal(skills.CanDualWield(inventory), requirements.CanDualWield(inventory));
        Assert.Equal(skills.SkillValue(inventory, 43), requirements.SkillValue(inventory, 43));
        Assert.Equal(skills.HasSpell(inventory, 1), requirements.HasSpell(inventory, 1));
        Assert.Equal(skills.HonorRank(inventory), requirements.HonorRank(inventory));
        Assert.Equal((uint)ReputationRank.Neutral, requirements.ReputationRank(inventory, BootyBay));
        Assert.Equal((uint)ReputationRank.Neutral, requirements.ReputationRank(inventory, UnknownFaction));
    }

    [Fact]
    public void UntrackedPlayer_IsNeutral_LikeAFactionWithoutAStanding()
    {
        Player stranger = TestWorld.CreatePlayer(5, 0, 0, new FakeSession());
        var requirements = new ReputationItemRequirements(DefaultItemRequirements.Instance, _service);
        Assert.Equal((uint)ReputationRank.Neutral, requirements.ReputationRank(stranger.Inventory, BootyBay));
    }
}
