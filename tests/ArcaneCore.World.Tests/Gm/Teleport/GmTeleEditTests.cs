using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Teleport;

/// <summary>Persistent game_tele edits, from vmangos HandleTeleAddCommand/HandleTeleDelCommand.</summary>
public sealed class GmTeleEditTests
{
    [Fact]
    public void EditCommands_RequireTheVmangosDeveloperRank()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.Null(table.Resolve("tele add", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("tele del", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("tele add", AccountSecurity.Administrator));
        Assert.NotNull(table.Resolve("tele del", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task TeleAdd_PersistsCurrentPosition_AndRejectsAnExistingName()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TELEADD", "Teleadd", AccountSecurity.Administrator);
        await host.PlaceAsync("Teleadd", -8800f, -120f, 90f);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele add My Saved Spot");
        Assert.Equal("Teleport location added.", (await gm.ReadChatAsync()).Text);
        Assert.Equal((-8800f, -120f, 90f, 0u), await host.OnWorldAsync(() =>
        {
            var location = WorldMaps.Of(host.World).FindGameTele("My Saved Spot")!;
            return (location.X, location.Y, location.Z, location.MapId);
        }));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele add Saved");
        Assert.Equal("Teleport location already exists!", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task TeleDel_RequiresTheExactName_AndRemovesTheStoredLocation()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("TELEDEL", "Teledel", AccountSecurity.Administrator);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele del storm");
        Assert.Equal("Teleport location not found!", (await gm.ReadChatAsync()).Text);
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".tele del STORMWIND");
        Assert.Equal("Teleport location deleted.", (await gm.ReadChatAsync()).Text);
        Assert.Null(await host.OnWorldAsync(() => WorldMaps.Of(host.World).FindGameTele("Stormwind")));
    }
}
