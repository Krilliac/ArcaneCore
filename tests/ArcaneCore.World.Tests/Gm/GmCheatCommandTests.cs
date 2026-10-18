using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets.Control;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.cheat, .hover and .gm visible (vmangos CharacterCommands.cpp HandleCheat*Command, HandleHoverCommand, HandleGMVisibleCommand).</summary>
public sealed class GmCheatCommandTests
{
    private const string Link = "|cffffffff|Hplayer:Cheatgm|h[Cheatgm]|h|r";

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach ((string path, int level) in new[]
        {
            ("cheat god", 3), ("cheat cooldown", 3), ("cheat casttime", 3), ("cheat power", 3), ("cheat waterwalk", 3),
            ("cheat immunepc", 3), ("cheat immunenpc", 3), ("cheat untargetable", 3), ("cheat status", 3), ("hover", 2), ("gm visible", 3),
        })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            ChatCommand? command = table.Resolve(path, AccountSecurity.GameMaster);
            Assert.NotNull(command);
            Assert.Equal(level, command.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task Cheats_ToggleOnTheInvoker_AndStatusListsThem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CHEATGM", "Cheatgm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat god maybe");
        Assert.Equal("Incorrect value, use on or off", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat god on");
        Assert.Equal($"You set god mode to on for {Link}.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat cooldown on");
        Assert.Equal($"You set no cooldown mode to on for {Link}.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat casttime on");
        Assert.Equal($"You set instant casting mode to on for {Link}.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat power on");
        Assert.Equal($"You set no power cost mode to on for {Link}.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat untargetable on");
        Assert.Equal($"You set untargetable mode to on for {Link}.", (await gm.ReadChatAsync()).Text);

        (uint threshold, PlayerCheats cheats, UnitFlags flags) = await host.PlayerStateAsync("Cheatgm", p => (p.InvincibilityHpThreshold, p.Cheats, p.UnitFlags));
        Assert.Equal(1u, threshold);
        Assert.Equal(PlayerCheats.NoCooldown | PlayerCheats.NoCastTime | PlayerCheats.NoPowerCost, cheats);
        Assert.NotEqual(0u, (uint)(flags & UnitFlags.NotSelectable));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat status");
        string[] expected = ["Cheats active on Cheatgm:", "- God", "- No cooldowns", "- No cast time", "- No power costs", "- Untargetable"];
        foreach (string line in expected)
        {
            Assert.Equal(line, (await gm.ReadChatAsync()).Text);
        }

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat god off");
        Assert.Equal($"You set god mode to off for {Link}.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat untargetable off");
        await gm.ReadChatAsync();
        (threshold, flags) = await host.PlayerStateAsync("Cheatgm", p => (p.InvincibilityHpThreshold, p.UnitFlags));
        Assert.Equal(0u, threshold);
        Assert.Equal(0u, (uint)(flags & UnitFlags.NotSelectable));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".hover");
        Assert.Equal("Hover enabled", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".hover 0");
        Assert.Equal("Hover disabled", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task CheatOnAnotherPlayer_TellsThem()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CHEATGM", "Cheatgm", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("CHEATPC", "Cheatpc", AccountSecurity.Player);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat immunenpc on Cheatpc");
        Assert.Equal("You set immune to creatures mode to on for |cffffffff|Hplayer:Cheatpc|h[Cheatpc]|h|r.", (await gm.ReadChatAsync()).Text);
        Assert.Equal($"Your immune to creatures mode has been turned on by {Link}.", (await player.ReadChatAsync()).Text);
        UnitFlags flags = await host.PlayerStateAsync("Cheatpc", p => p.UnitFlags);
        Assert.NotEqual(0u, (uint)(flags & UnitFlags.ImmuneToNpc));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".cheat god on Nobodyhere");
        Assert.Equal("Player not found!", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task GmVisibleOff_HidesTheGmFromLowerSecurity()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("CHEATGM", "Cheatgm", AccountSecurity.GameMaster);
        await using WorldTestClient player = await host.EnterWorldAsync("CHEATPC", "Cheatpc", AccountSecurity.Player);
        ObjectGuid gmGuid = await host.PlayerStateAsync("Cheatgm", p => p.Guid);
        ObjectGuid pcGuid = await host.PlayerStateAsync("Cheatpc", p => p.Guid);
        Assert.True(await SeesAsync(host, "Cheatpc", gmGuid, expected: true));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm visible off");
        await gm.ReadUntilAsync(WorldOpcode.SmsgNotification);
        Assert.True(await SeesAsync(host, "Cheatpc", gmGuid, expected: false));
        Assert.True(await SeesAsync(host, "Cheatgm", pcGuid, expected: true));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm visible");
        Assert.Equal("You are: invisible", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gm visible on");
        await gm.ReadUntilAsync(WorldOpcode.SmsgNotification);
        Assert.True(await SeesAsync(host, "Cheatpc", gmGuid, expected: true));
    }

    private static async Task<bool> SeesAsync(WorldTestHost host, string viewer, ObjectGuid target, bool expected)
    {
        for (int i = 0; i < 50; i++)
        {
            if (await host.PlayerStateAsync(viewer, p => p.VisibleObjects.Contains(target)) == expected)
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }
}