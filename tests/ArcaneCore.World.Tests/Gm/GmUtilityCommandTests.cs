using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.stable, .bank, .dismount, .demorph, .combatstop, .freeze and .unfreeze (vmangos MiscCommands/CharacterCommands/UnitCommands).</summary>
public sealed class GmUtilityCommandTests
{
    [Fact]
    public void Commands_UseTheVmangosRetailLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("dismount", AccountSecurity.Player));
        foreach (string path in new[] { "stable", "bank", "demorph", "combatstop", "freeze", "unfreeze" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Trait("Category", "Timing")]
    [Fact]
    public async Task Dismount_RepliesWhenNotMounted_AndClearsTheMount()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient player = await host.EnterWorldAsync("DISMOUNT", "Dismounter");

        await player.SendChatAsync(ChatType.Say, Language.Common, ".dismount");
        Assert.Equal(GmUtilityCommands.NotMounted, (await player.ReadChatAsync()).Text);

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dismounter")!.SetUInt32(UpdateFields.UnitFieldMountdisplayid, 2410));
        await player.SendChatAsync(ChatType.Say, Language.Common, ".dismount");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Dismounter")!.GetUInt32(UpdateFields.UnitFieldMountdisplayid) == 0, "dismount");
    }

    [Fact]
    public async Task Demorph_RestoresTheNativeDisplay()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DEMORPH", "Demorpher", AccountSecurity.GameMaster);
        uint native = await host.PlayerStateAsync("Demorpher", p => p.NativeDisplayId);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Demorpher")!.DisplayId = native + 7);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".demorph");

        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Demorpher")!.DisplayId == native, "demorph");
    }

    [Fact]
    public async Task Bank_OpensTheOwnBank()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("BANKGM", "Bankgm", AccountSecurity.GameMaster);
        ulong guid = (await host.PlayerAsync("Bankgm")).Guid.Value;

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".bank");

        byte[] show = await gm.ReadUntilAsync(WorldOpcode.SmsgShowBank);
        Assert.Equal(guid, BitConverter.ToUInt64(show, 0));
        Assert.True(await host.PlayerStateAsync("Bankgm", p => p.Inventory.CanUseBank?.Invoke() == true));
    }

    [Fact]
    public async Task Stable_TellsANonHunterThereIsNoStable()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("STABLEGM", "Stablegm", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".stable");

        string reply = (await gm.ReadChatAsync()).Text;
        Assert.True(reply is GmUtilityCommands.StableNotHunter or GmUtilityCommands.StableUnavailable, reply);
    }
}