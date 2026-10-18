using ArcaneCore.Game;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.modify swim, bwalk, aspeed, drunk, morph and emotestate (vmangos CharacterCommands.cpp / UnitCommands.cpp, Chat.cpp:588-601).</summary>
public sealed class GmModifyMoreCommandTests
{
    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        // Retail level 2 (SEC_TICKETMASTER) and 3 (SEC_GAMEMASTER): both above a moderator (level 1), both open to a game master.
        foreach ((string path, int level) in new[] { ("modify swim", 2), ("modify bwalk", 2), ("modify aspeed", 2), ("modify drunk", 2), ("modify morph", 3), ("modify emotestate", 3) })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            ChatCommand? command = table.Resolve(path, AccountSecurity.GameMaster);
            Assert.NotNull(command);
            Assert.Equal(level, command.RequiredLevel(table.Gm));
        }
    }

    [Fact]
    public async Task SpeedDrunkAndMorph_ChangeTheInvoker()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("MODGM", "Modgm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify drunk 50");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify morph 1060");
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify swim 9");
        // Below basic admin the rate is capped at 4.
        Assert.Equal("You set the swim speed to 4.00 from normal of |cffffffff|Hplayer:Modgm|h[Modgm]|h|r.", (await gm.ReadChatAsync()).Text);

        (ushort drunk, uint display) = await host.PlayerStateAsync("Modgm", p => (SpellSystem.GetDrunkValue(p), p.DisplayId));
        Assert.Equal((ushort)(50 * 0xFFFF / 100 & 0xFFFE), drunk);
        Assert.Equal(1060u, display);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify aspeed 2");
        Assert.Equal("You set all speeds to 2.00 from normal of Modgm.", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".modify bwalk 0.05");
        Assert.Equal("Incorrect values.", (await gm.ReadChatAsync()).Text);
    }
}
