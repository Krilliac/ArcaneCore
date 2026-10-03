using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.WorldState;
using ArcaneCore.Game.WorldState.Exploration;
using ArcaneCore.Game.WorldState.Zones;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.Protocol;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>Exploration in the running world: discovery, XP and packets, and the GM commands (vmangos Player.cpp:6089-6204, CharacterCommands.cpp:640-830).</summary>
public sealed class ExplorationWorldTests
{
    private sealed class FlagLocator : IZoneLocator
    {
        public volatile uint Flag = 35; // word 1, bit 3

        public bool CanDeriveZones => true;

        public (uint ZoneId, uint AreaId) Locate(Map map, Player player) => (12, 87);

        public AreaTemplate? Find(uint areaId) => areaId switch
        {
            87 => new AreaTemplate(87, 0, 12, 35, 0, 1, "Goldshire", 0, 0),
            99 => new AreaTemplate(99, 0, 12, 5000, 0, 1, "Out of range", 0, 0),
            12 => new AreaTemplate(12, 0, 0, 41, 0, 1, "Elwynn Forest", 0, 0),
            _ => null,
        };

        public uint GetAreaFlag(Map map, Player player) => Flag;

        public AreaTemplate? FindByAreaFlag(uint areaFlag, uint mapId) => areaFlag == 35 ? Find(87) : null;
    }

    private static WorldTestHost Start(out FlagLocator locator)
    {
        WorldTestHost host = WorldTestHost.Start();
        locator = new FlagLocator();
        WorldStateHooks.For(host.World).Locator = locator;
        host.WorldServices.GetRequiredService<ExplorationFeature>().ReplaceBaseXp([new ExplorationBaseXpRecord(1, 5), new ExplorationBaseXpRecord(2, 15)]);
        return host;
    }

    private static async Task<string> ReplyAsync(WorldTestClient client, string line)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, line);
        ChatMessage reply = await client.ReadChatAsync();
        Assert.Equal(ChatType.System, reply.Type);
        return reply.Text;
    }

    private static Task<uint[]> WordsAsync(WorldTestHost host, string name)
        => host.PlayerStateAsync(name, ExploredZonesFields.Read);

    [Fact]
    public async Task NewCharacter_GetsOneExplorationPacketRightAfterLogin_AndNoRepeat()
    {
        await using WorldTestHost host = Start(out _);
        await using WorldTestClient client = await host.EnterWorldAsync("EXPLO", "Explo");

        // vmangos: LOG_XPGAIN (GiveXP), then SMSG_EXPLORATION_EXPERIENCE (area 87, base(1) = 5 xp). The
        // first-login cinematic is not delivered, so the check runs right after the map add.
        List<(WorldOpcode Opcode, byte[] Payload)> packets = await client.CollectAsync();
        (WorldOpcode Opcode, byte[] Payload)[] relevant = [.. packets.Where(p => p.Opcode is WorldOpcode.SmsgLogXpgain or WorldOpcode.SmsgExplorationExperience)];
        Assert.Equal([WorldOpcode.SmsgLogXpgain, WorldOpcode.SmsgExplorationExperience], relevant.Select(p => p.Opcode));
        Assert.Equal([87, 0, 0, 0, 5, 0, 0, 0], relevant[1].Payload);
        Assert.Equal(0x8u, (await WordsAsync(host, "Explo"))[1]);

        Assert.DoesNotContain(await client.CollectAsync(TimeSpan.FromMilliseconds(300)), p => p.Opcode == WorldOpcode.SmsgExplorationExperience);
    }

    [Fact]
    public async Task ExploreCheat_AsVmangosWritesIt_AnnouncesTheTarget_ButChangesTheIssuer()
    {
        await using WorldTestHost host = Start(out _);
        await using WorldTestClient gm = await host.EnterWorldAsync("GMEXP", "Gmexp", AccountSecurity.Moderator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHEREXP", "Otherexp");
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes((await host.PlayerAsync("Otherexp")).Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Gmexp")!.Selection.Value == host.World.FindOnlinePlayer("Otherexp")!.Guid.Value, "the selection");

        string reply = await ReplyAsync(gm, ".explorecheat 1");

        Assert.Equal("|Hplayer:Otherexp|h[Otherexp]|h has explored all zones now.", reply);
        Assert.All(await WordsAsync(host, "Gmexp"), w => Assert.Equal(0xFFFFFFFFu, w));
        Assert.DoesNotContain(await WordsAsync(host, "Otherexp"), w => w == 0xFFFFFFFFu); // the vmangos quirk

        Assert.Equal("|Hplayer:Otherexp|h[Otherexp]|h has no more explored zones.", await ReplyAsync(gm, ".explorecheat 0"));
        Assert.All(await WordsAsync(host, "Gmexp"), w => Assert.Equal(0xFFFFFFFFu, w)); // "0" ORs in 0: nothing is cleared
    }

    [Fact]
    public async Task ExploreCheat_CorrectedByConfig_HitsTheTarget_AndReallyClears()
    {
        await using WorldTestHost host = Start(out _);
        WorldStateHooks.For(host.World).ExplorationSettings.CorrectExploreCheat = true;
        await using WorldTestClient gm = await host.EnterWorldAsync("GMFIX", "Gmfix", AccountSecurity.Moderator);
        await using WorldTestClient other = await host.EnterWorldAsync("OTHERFIX", "Otherfix");
        await gm.SendAsync(WorldOpcode.CmsgSetSelection, BitConverter.GetBytes((await host.PlayerAsync("Otherfix")).Guid.Value));
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Gmfix")!.Selection.Value == host.World.FindOnlinePlayer("Otherfix")!.Guid.Value, "the selection");

        await ReplyAsync(gm, ".explorecheat 1");
        Assert.All(await WordsAsync(host, "Otherfix"), w => Assert.Equal(0xFFFFFFFFu, w));
        await ReplyAsync(gm, ".explorecheat 0");
        Assert.All(await WordsAsync(host, "Otherfix"), w => Assert.Equal(0u, w));
    }

    [Fact]
    public async Task ShowArea_SetsTheAreasBit_HideAreaToggles_AndBadValuesAreRefused()
    {
        await using WorldTestHost host = Start(out FlagLocator locator);
        locator.Flag = ExploredZones.NoAreaFlag; // nothing is discovered by walking
        await using WorldTestClient gm = await host.EnterWorldAsync("GMAREA", "Gmarea", AccountSecurity.Moderator);
        await gm.CollectAsync();

        Assert.Equal(ExplorationCommands.ExploreAreaText, await ReplyAsync(gm, ".showarea 87"));
        uint[] words = await WordsAsync(host, "Gmarea");
        Assert.Equal(0x8u, words[1]);
        Assert.Equal(1, words.Count(w => w != 0));

        Assert.Equal(ExplorationCommands.UnexploreAreaText, await ReplyAsync(gm, ".hidearea 87"));
        Assert.Equal(0u, (await WordsAsync(host, "Gmarea"))[1]);
        await ReplyAsync(gm, ".hidearea 87"); // XOR: hiding an unexplored area explores it (vmangos)
        Assert.Equal(0x8u, (await WordsAsync(host, "Gmarea"))[1]);

        Assert.Equal(ExplorationCommands.BadValueText, await ReplyAsync(gm, ".showarea 99"));   // explore flag 5000: word 156
        Assert.Equal(ExplorationCommands.BadValueText, await ReplyAsync(gm, ".showarea 4242")); // no such area (GetFlagById is -1)
        Assert.Contains("Syntax", await ReplyAsync(gm, ".showarea"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_NeedStaff()
    {
        await using WorldTestHost host = Start(out _);
        await using WorldTestClient player = await host.EnterWorldAsync("NOSTAFF", "Nostaff");
        await player.CollectAsync();

        string reply = await ReplyAsync(player, ".showarea 87");

        Assert.NotEqual(ExplorationCommands.ExploreAreaText, reply);
    }
}
