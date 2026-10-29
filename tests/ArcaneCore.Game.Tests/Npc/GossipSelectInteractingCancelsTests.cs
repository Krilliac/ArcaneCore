using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// vmangos HandleGossipSelectOptionOpcode (NPCHandler.cpp:372-377) ends the channel and the INTERACTING auras right after the coded-option
/// check and before it looks the NPC up (:387), so a select for an unreachable NPC still cancels while a coded option sent without its
/// code returns first.
/// </summary>
public sealed class GossipSelectInteractingCancelsTests
{
    private static NpcServiceTestKit Kit(byte boxCoded = 0)
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = 0, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Option", BoxCoded = boxCoded },
            ],
        };
        return new NpcServiceTestKit(NpcFlags.Gossip, content);
    }

    [Fact]
    public void GossipSelect_ForAnOutOfRangeNpc_StillEndsInteractingAuras()
    {
        using NpcServiceTestKit kit = Kit();
        int cancels = 0;
        kit.Services.InteractingCancels = _ => cancels++;
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid); // the menu is open
        Assert.Equal(1, cancels);

        kit.Npc = kit.Npc with { X = kit.Player.X + 60 };
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);

        Assert.Equal(2, cancels);
    }

    [Fact]
    public void GossipSelect_ForANonInteractableNpc_StillEndsInteractingAuras()
    {
        using NpcServiceTestKit kit = Kit();
        int cancels = 0;
        kit.Services.InteractingCancels = _ => cancels++;
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        cancels = 0;

        kit.Services.GossipSelectOption(kit.Player, ObjectGuid.WithEntry(HighGuid.Unit, 424242, 1), 0, null);

        Assert.Equal(1, cancels);
    }

    [Fact]
    public void CodedGossipOption_SentWithoutItsCode_DoesNotCancel()
    {
        using NpcServiceTestKit kit = Kit(boxCoded: 1);
        int cancels = 0;
        kit.Services.InteractingCancels = _ => cancels++;
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        cancels = 0;
        Assert.True(kit.Services.IsGossipOptionCoded(kit.Player, 0), "setup: the option is coded");

        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, "");
        Assert.Equal(0, cancels);

        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, "code");
        Assert.Equal(1, cancels);
    }
}
