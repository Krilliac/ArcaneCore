using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Game.Talents;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// GOSSIP_OPTION_UNLEARNTALENTS. vmangos offers it only when Creature::CanTrainAndResetTalentsOf holds
/// (Creature.cpp:1523-1527, Player.cpp:12036-12039) and selecting it closes the gossip and asks for the wipe
/// confirmation (Player.cpp:12242-12245). The shipped data reaches the option through two menus
/// (classic-db: menu 4660 option 1 -> action menu 4461, whose option 16 is the real entry).
/// </summary>
public sealed class NpcTalentGossipTests
{
    private const uint TrainerMenu = 4660;
    private const uint UnlearnMenu = 4461;

    private static GossipMenuOption Unlearn(uint menu, uint id)
        => new() { MenuId = menu, Id = id, OptionId = (byte)GossipOption.UnlearnTalents, NpcOptionNpcFlag = (uint)NpcFlags.Trainer, OptionText = "Unlearn" };

    private static NpcServiceKit Kit(NpcContent content, byte playerLevel = 10, TrainerType type = TrainerType.Class, Class trainerClass = Class.Warrior)
    {
        var kit = new NpcServiceKit(NpcFlags.Gossip | NpcFlags.Trainer, content);
        kit.Player.Level = playerLevel;
        kit.Npc = kit.Npc with { TrainerType = type, TrainerClass = (byte)trainerClass };
        return kit;
    }

    private static uint OptionCount(byte[] gossipMessage)
    {
        var reader = new PacketReader(gossipMessage);
        reader.ReadUInt64();
        reader.ReadUInt32();
        return reader.ReadUInt32();
    }

    [Theory]
    [InlineData(9, TrainerType.Class, Class.Warrior, false)]
    [InlineData(10, TrainerType.Class, Class.Warrior, true)]
    [InlineData(60, TrainerType.Class, Class.Warrior, true)]
    [InlineData(60, TrainerType.Class, Class.Mage, false)]
    [InlineData(60, TrainerType.Mounts, Class.Warrior, false)]
    [InlineData(60, TrainerType.TradeSkills, Class.Warrior, false)]
    [InlineData(60, TrainerType.Pets, Class.Warrior, false)]
    public void CanTrainAndResetTalentsOf_FollowsVmangos(byte level, TrainerType type, Class trainerClass, bool expected)
    {
        using NpcServiceKit kit = Kit(NpcContent.Empty, level, type, trainerClass);
        Assert.Equal(expected, TalentTrainerRules.CanTrainAndResetTalentsOf(kit.Player, kit.Npc));
    }

    [Fact]
    public void Option_StaysHiddenUntilAnOwnerSuppliesThePredicate()
    {
        NpcContent content = NpcContent.Empty with { GossipMenuOptions = [Unlearn(0, 0)] };
        using NpcServiceKit kit = Kit(content);
        Assert.Null(kit.Services.UnlearnTalentsOffered);

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Equal(0u, OptionCount(kit.Single(WorldOpcode.SmsgGossipMessage)));
    }

    [Fact]
    public void Option_IsShownWhenThePredicateAllows_AndHiddenWhenItDoesNot()
    {
        NpcContent content = NpcContent.Empty with { GossipMenuOptions = [Unlearn(0, 0)] };
        using NpcServiceKit kit = Kit(content);
        bool offer = true;
        kit.Services.UnlearnTalentsOffered = (_, _) => offer;

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Equal(1u, OptionCount(kit.Single(WorldOpcode.SmsgGossipMessage)));

        offer = false;
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Equal(0u, OptionCount(kit.Single(WorldOpcode.SmsgGossipMessage)));
    }

    [Fact]
    public void Predicate_ReceivesThePlayerAndTheNpc()
    {
        NpcContent content = NpcContent.Empty with { GossipMenuOptions = [Unlearn(0, 0)] };
        using NpcServiceKit kit = Kit(content);
        (Player, NpcInfo)? seen = null;
        kit.Services.UnlearnTalentsOffered = (p, n) =>
        {
            seen = (p, n);
            return TalentTrainerRules.CanTrainAndResetTalentsOf(p, n);
        };

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Equal(1u, OptionCount(kit.Single(WorldOpcode.SmsgGossipMessage)));
        Assert.Same(kit.Player, seen!.Value.Item1);
        Assert.Equal(kit.Npc.Guid, seen.Value.Item2.Guid);
    }

    [Fact]
    public void Selecting_ClosesTheGossip_AndRaisesTheForeignOptionOnce()
    {
        NpcContent content = NpcContent.Empty with { GossipMenuOptions = [Unlearn(0, 0)] };
        using NpcServiceKit kit = Kit(content);
        kit.Services.UnlearnTalentsOffered = TalentTrainerRules.CanTrainAndResetTalentsOf;
        var raised = new List<(Player, NpcInfo, GossipOption)>();
        kit.Services.ForeignOptionSelected += (p, n, o) => raised.Add((p, n, o));
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Drain();

        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);

        Assert.Single(kit.Drain(), p => p.Opcode == WorldOpcode.SmsgGossipComplete);
        (Player player, NpcInfo npc, GossipOption option) = Assert.Single(raised);
        Assert.Same(kit.Player, player);
        Assert.Equal(kit.Npc.Guid, npc.Guid);
        Assert.Equal(GossipOption.UnlearnTalents, option);
    }

    [Fact]
    public void ClassTrainerMenuPair_ReachesTheUnlearnOption()
    {
        // classic-db: menu 4660 line "I wish to unlearn my talents." (option 1, action menu 4461); menu 4461 holds option 16.
        NpcContent content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = TrainerMenu, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip, OptionText = "I wish to unlearn my talents.", ActionMenuId = (int)UnlearnMenu },
                Unlearn(UnlearnMenu, 0),
            ],
        };
        using NpcServiceKit kit = Kit(content);
        kit.Npc = kit.Npc with { GossipMenuId = TrainerMenu };
        kit.Services.UnlearnTalentsOffered = TalentTrainerRules.CanTrainAndResetTalentsOf;

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Equal(1u, OptionCount(kit.Single(WorldOpcode.SmsgGossipMessage)));
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);

        var menu = new PacketReader(kit.Single(WorldOpcode.SmsgGossipMessage));
        menu.ReadUInt64();
        menu.ReadUInt32();
        Assert.Equal(1u, menu.ReadUInt32());                  // the re-prepared menu 4461 now shows its 16 option
        Assert.Equal(UnlearnMenu, kit.State.Menu.MenuId);
        Assert.Equal(GossipOption.UnlearnTalents, kit.State.Menu.GossipItems[0].OptionId);
    }
}
