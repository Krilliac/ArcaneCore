using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Game.Tests.Npc;

/// <summary>
/// The gossip DB scripts (world schema 42) as the quest service raises them for the creature systems: a selected option's
/// <c>gossip_menu_option.action_script_id</c> after the option's own action, the creature as the source (mangos-classic Player::OnGossipSelect,
/// Player.cpp:11953-11960), and the chosen text's <c>gossip_menu.script_id</c> when the menu is shown, the player as the source
/// (Player::GetGossipTextId, :11979-12004). The ids are classic-db z2815's: option 1 of menu 21 runs script 21 (QUEST_EXPLORED 6981),
/// menu 1405's text 2039 runs script 1405 (the Screecher Spirit's kill credit); the menus here are shortened to those lines.
/// </summary>
public sealed class GossipDbScriptTests
{
    [Fact]
    public void SelectingAScriptedOption_StartsItsScript_WithTheCreatureAsTheSource()
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenuOptions =
            [
                new GossipMenuOption { MenuId = 0, Id = 0, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Can you tell me about this shard?", ActionScriptId = 21 },
                new GossipMenuOption { MenuId = 0, Id = 1, OptionId = (byte)GossipOption.Gossip, NpcOptionNpcFlag = (uint)NpcFlags.Gossip,
                    OptionText = "Unscripted" },
            ],
        };
        using var kit = new NpcServiceKit(NpcFlags.Gossip, content);
        var started = new List<(Player Player, ObjectGuid Source, uint Script, bool PlayerIsSource)>();
        kit.Services.GossipScriptStarted += (player, npc, script, playerIsSource) => started.Add((player, npc, script, playerIsSource));

        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Empty(started); // the default menu has no text script
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 1, null);
        Assert.Empty(started);
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        kit.Services.GossipSelectOption(kit.Player, kit.Npc.Guid, 0, null);

        Assert.Equal([(kit.Player, kit.Npc.Guid, 21u, false)], started);
    }

    [Fact]
    public void ShowingAMenuWhoseTextHasAScript_StartsIt_WithThePlayerAsTheSource()
    {
        NpcContent content = NpcContent.Empty with
        {
            GossipMenus = [new GossipMenu { Entry = 1405, TextId = 2039, ScriptId = 1405 }, new GossipMenu { Entry = 1406, TextId = 2040 }],
        };
        using var kit = new NpcServiceKit(NpcFlags.Gossip, content);
        var started = new List<(ObjectGuid Source, uint Script, bool PlayerIsSource)>();
        kit.Services.GossipScriptStarted += (_, npc, script, playerIsSource) => started.Add((npc, script, playerIsSource));

        kit.Npc = kit.Npc with { GossipMenuId = 1406 };
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);
        Assert.Empty(started);
        kit.Npc = kit.Npc with { GossipMenuId = 1405 };
        kit.Services.GossipHello(kit.Player, kit.Npc.Guid);

        Assert.Equal([(kit.Npc.Guid, 1405u, true)], started);
    }
}
