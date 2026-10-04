using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Death;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Death;

/// <summary>.revive, .gocorpse and .neargrave (vmangos CharacterCommands.cpp:615-635, TeleportCommands.cpp:1347-1366, MiscCommands.cpp:1900-1965).</summary>
public sealed class DeathGmCommandTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    private sealed class MapStore : IMapDataStore
    {
        public Task<MapContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new MapContent(
            [new MapTemplate(0, 0, MapType.Common, 12, 0, 0, -1, 0, 0, "Eastern Kingdoms", ""), new MapTemplate(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", "")],
            [new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 2, 0)],
            [], [], []));
    }

    private sealed class GraveyardStore(GraveyardContent content) : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }

    private static WorldTestHost Start(params GraveyardLink[] links) => WorldTestHost.Start(configureServices: services =>
    {
        services.AddSingleton<IMapDataStore, MapStore>();
        services.AddSingleton<IGraveyardDataStore>(new GraveyardStore(new GraveyardContent(
            [new WorldSafeLoc(1, 0, -8800f, -100f, 90f, 1.5f, "Goldshire"), new WorldSafeLoc(2, 0, -9000f, -200f, 90f, 0f, "Elsewhere")], links)));
    });

    private static async Task<string> ReplyAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string name in new[] { "revive", "gocorpse", "neargrave" })
        {
            Assert.Null(table.Resolve(name, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(name, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task NearGrave_NamesTheNearestLinkedGraveyard_AndItsTeam()
    {
        await using WorldTestHost host = Start(new GraveyardLink(1, 12, 0), new GraveyardLink(2, 12, 469));
        await using WorldTestClient gm = await host.EnterWorldAsync("DGMNEAR", "Dgmnear", AccountSecurity.Administrator);
        await host.PlaceAsync("Dgmnear", -8790f, -95f, 90f);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dgmnear")!.ZoneId = 12);
        await gm.CollectAsync(Quiet);

        Assert.Equal("Graveyard #1 (faction: any) is nearest from linked to zone #12.", await ReplyAsync(gm, ".neargrave"));
        Assert.Equal("Graveyard #1 (faction: any) is nearest from linked to zone #12.", await ReplyAsync(gm, ".neargrave horde"));
    }

    [Fact]
    public async Task NearGrave_ForATeamWithNoGraveyard_SaysSo()
    {
        await using WorldTestHost host = Start(new GraveyardLink(2, 12, 469));
        await using WorldTestClient gm = await host.EnterWorldAsync("DGMNEAR2", "Dgmneartwo", AccountSecurity.Administrator);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Dgmneartwo")!.ZoneId = 12);
        await gm.CollectAsync(Quiet);

        Assert.Equal("Zone #12 doesn't have linked graveyards for faction: horde.", await ReplyAsync(gm, ".neargrave h"));
        Assert.StartsWith("Graveyard #2 (faction: alliance)", await ReplyAsync(gm, ".neargrave alliance"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revive_BringsADeadPlayerBackAtHalfHealth_AndTheCorpseGoes()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DGMREV", "Dgmrev", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("DGMREVV", "Dgmrevvictim");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Dgmrevvictim")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
        });
        await gm.CollectAsync(Quiet);

        string reply = await ReplyAsync(gm, ".revive dgmrevvictim");

        Assert.Equal("Character |cffffffff|Hplayer:Dgmrevvictim|h[Dgmrevvictim]|h|r revived.", reply);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Dgmrevvictim")!.IsAlive, "the player to be revived");
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Dgmrevvictim")!;
            Assert.Equal(player.MaxHealth / 2, player.Health);
            Assert.Null(player.Combat.Corpse);
        });
    }

    [Fact]
    public async Task GoCorpse_TeleportsToTheCorpse_AndSaysWhenThereIsNone()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("DGMGOC", "Dgmgoc", AccountSecurity.Administrator);
        await using WorldTestClient ghost = await host.EnterWorldAsync("DGMGOCG", "Dgmghost");
        await host.PlaceAsync("Dgmghost", -8700f, -50f, 91f);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer("Dgmghost")!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
        });
        await gm.CollectAsync(Quiet);

        Assert.Equal("Teleport location not found!", await ReplyAsync(gm, ".gocorpse dgmgoc"));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".gocorpse dgmghost");
        var reader = new PacketReader(await gm.ReadUntilAsync(WorldOpcode.MsgMoveTeleportAck));
        reader.ReadPackedGuid();
        reader.ReadUInt32();
        MovementInfo info = MovementInfo.Read(ref reader);
        Assert.Equal((-8700f, -50f, 91f), (info.X, info.Y, info.Z));
    }
}
