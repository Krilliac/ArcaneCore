using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// <c>.honor add|addkill|show|setrp|reset</c> and <c>.modify honor</c> (vmangos CharacterCommands.cpp:2321-2570; Chat.cpp:467-475, 596).
/// Accounts are Administrator so the tests hold under any security map; the access check is on the command table.
/// </summary>
public sealed class HonorCommandTests
{
    private static async Task<string> Run(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    // A usage reply is the command's multi-line help: take the first line and drain the rest so it cannot leak into the next read.
    private static async Task<string> Usage(WorldTestClient client, string command)
    {
        string first = await Run(client, command);
        await client.CollectAsync();
        return first;
    }

    private static async Task SelectAsync(WorldTestHost host, string caller, string target)
    {
        Player targetPlayer = await host.PlayerAsync(target);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(caller)!.Selection = targetPlayer.Guid);
    }

    [Fact]
    public void The_group_and_its_subcommands_follow_the_vmangos_levels()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "honor add", "honor addkill", "honor setrp", "honor reset", "honor show", "modify honor" })
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Player));
            Assert.NotNull(table.Resolve(path, AccountSecurity.Administrator));
        }

        Assert.Null(table.Resolve("honor add", AccountSecurity.Moderator));   // BASIC_ADMIN
        Assert.Null(table.Resolve("modify honor", AccountSecurity.Moderator));
    }

    [Fact]
    public async Task Add_gives_the_selected_player_honor_from_nowhere_and_ignores_a_zero_or_nonsense_amount()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCGM", "Hcgm", AccountSecurity.Administrator);
        await using WorldTestClient other = await host.EnterWorldAsync("HCOTHER", "Hcother");
        await gm.CollectAsync();
        await SelectAsync(host, "Hcgm", "Hcother");

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".honor add 100.5");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Hcother")!.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution) == 100, "the honor to arrive");
        // The target hears of it through the PvP credit packet, which names no victim.
        byte[] credit = await other.ReadUntilAsync(WorldOpcode.SmsgPvpCredit);
        Assert.Equal(100, BitConverter.ToInt32(credit, 0));
        Assert.Equal(0UL, BitConverter.ToUInt64(credit, 4));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".honor add abc");   // atof("abc") = 0: nothing is added
        Assert.StartsWith("Syntax:", await Usage(gm, ".honor add"));                   // no argument: the usage line
        Assert.Equal(100u, await host.PlayerStateAsync("Hcother", p => p.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution)));
    }

    [Fact]
    public async Task Setrp_sets_the_rank_points_and_updates_the_honor_tab_and_reset_forgets_everything()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCRP", "Hcrp", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal("You have changed rank points of Hcrp to 12000.", await Run(gm, ".honor setrp 12000"));
        Assert.Equal((byte)8, await host.PlayerStateAsync("Hcrp", p => p.GetByte(UpdateFields.PlayerBytes3, 3)));   // internal rank 8
        Assert.Equal((byte)8, await host.PlayerStateAsync("Hcrp", p => p.GetByte(UpdateFields.PlayerFieldBytes, 3)));
        Assert.Equal("You have changed rank points of Hcrp to 1.5.", await Run(gm, ".honor setrp 1.5"));
        Assert.StartsWith("Syntax:", await Usage(gm, ".honor setrp nonsense"));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".honor setrp 12000");
        await gm.ReadChatAsync();
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".honor reset");
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer("Hcrp")!.GetByte(UpdateFields.PlayerBytes3, 3) == 0, "the reset");
        Assert.Equal((byte)0, await host.PlayerStateAsync("Hcrp", p => p.GetByte(UpdateFields.PlayerFieldBytes, 3)));
    }

    [Fact]
    public async Task Show_prints_the_retail_lines_with_the_rank_named_by_its_internal_number()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCSHOW", "Hcshow", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await Run(gm, ".honor setrp 12000"); // internal rank 8 = Master Sergeant (Alliance), visual 4

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".honor show");
        var lines = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            lines.Add((await gm.ReadChatAsync()).Text);
        }

        Assert.Equal("Player: Hcshow - Master Sergeant  (Rank 4)", lines[0]);
        Assert.Equal("Today: [Honorable Kills: |c0000ff000|r] [Dishonorable Kills: |c00ff00000|r]", lines[1]);
        Assert.Equal("Yesterday: [Kills: |c0000ff000|r] [Honor: 0]", lines[2]);
        Assert.Equal("This Week: [Kills: |c0000ff000|r] [Honor: 0]", lines[3]);
        Assert.Equal("Last Week: [Kills: |c0000ff000|r] [Honor: 0] [Standing: 0]", lines[4]);
        Assert.StartsWith("Life Time: [Rank Points: |c0000ff0012000.000000|r]", lines[5]);
        Assert.EndsWith("[Highest Rank 4: Master Sergeant ]", lines[5]);
    }

    [Fact]
    public async Task Modify_honor_writes_the_update_field_and_confirms_even_for_an_unknown_field()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCMOD", "Hcmod", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.Equal("The lastweekstanding field of Hcmod was set to 5", await Run(gm, ".modify honor lastweekstanding 5"));
        Assert.Equal(5u, await host.PlayerStateAsync("Hcmod", p => p.GetUInt32(UpdateFields.PlayerFieldLastWeekRank)));
        Assert.Equal("The lifetimehonorablekills field of Hcmod was set to 321", await Run(gm, ".modify honor lifetimehonorablekills 321"));
        Assert.Equal(321u, await host.PlayerStateAsync("Hcmod", p => p.GetUInt32(UpdateFields.PlayerFieldLifetimeHonorbaleKills)));
        Assert.Equal("The todaykills field of Hcmod was set to 7", await Run(gm, ".modify honor todaykills 7"));
        Assert.Equal(7u, await host.PlayerStateAsync("Hcmod", p => p.GetUInt16(UpdateFields.PlayerFieldSessionKills, 0)));
        Assert.Equal("The rank field of Hcmod was set to 6", await Run(gm, ".modify honor rank 6"));
        Assert.Equal((byte)6, await host.PlayerStateAsync("Hcmod", p => p.GetByte(UpdateFields.PlayerBytes3, 3)));
        Assert.Equal("The points field of Hcmod was set to 200", await Run(gm, ".modify honor points 200"));
        Assert.Equal((byte)200, await host.PlayerStateAsync("Hcmod", p => p.GetByte(UpdateFields.PlayerFieldBytes2, 0)));

        // Unknown field: no field changes, but vmangos still prints its confirmation (CharacterCommands.cpp:2534).
        Assert.Equal("The nosuchfield field of Hcmod was set to 9", await Run(gm, ".modify honor nosuchfield 9"));
    }

    [Fact]
    public async Task Modify_honor_refuses_out_of_range_values_and_missing_arguments_with_the_usage_line()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCMODBAD", "Hcmodbad", AccountSecurity.Administrator);
        await gm.CollectAsync();
        Assert.StartsWith("Syntax:", await Usage(gm, ".modify honor points 256"));
        Assert.StartsWith("Syntax:", await Usage(gm, ".modify honor points -1"));
        Assert.StartsWith("Syntax:", await Usage(gm, ".modify honor rank 19"));
        Assert.StartsWith("Syntax:", await Usage(gm, ".modify honor"));
        Assert.StartsWith("Syntax:", await Usage(gm, ".modify honor rank"));
    }

    [Fact]
    public async Task Addkill_on_yourself_shows_the_usage_and_on_an_unknown_selection_says_the_player_was_not_found()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCKILL", "Hckill", AccountSecurity.Administrator);
        await gm.CollectAsync();
        Assert.StartsWith("Syntax:", await Usage(gm, ".honor addkill"));

        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Hckill")!.Selection = new ObjectGuid(0xDEAD));
        Assert.Equal("Player not found!", await Run(gm, ".honor addkill"));
        Assert.Equal("Player not found!", await Run(gm, ".honor add 10"));
    }

    // Honor commands need level 4, which only a stored Administrator reaches by default, and nobody outranks one. Mapping GameMaster to
    // level 6 lets a lower-security GM run them so the target check is what is under test.
    private static WorldTestHost StartWithGameMasterAtLevel6(bool lowerSecurity = true)
        => WorldTestHost.Start(configureServices: services => services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["World:GmCommands:SecurityMap:GameMaster"] = "6",
                ["World:GmCommands:LowerSecurity"] = lowerSecurity ? "true" : "false",
            }).Build()));

    [Fact]
    public async Task A_lower_security_gm_cannot_setrp_reset_modify_or_add_honor_on_a_higher_security_target()
    {
        await using WorldTestHost host = StartWithGameMasterAtLevel6();
        await using WorldTestClient gm = await host.EnterWorldAsync("HCLOW", "Hclow", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("HCHIGH", "Hchigh", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();
        await SelectAsync(host, "Hclow", "Hchigh");

        // The admin starts at rank 8 with a field set, so a reset or a write would be visible.
        await Run(admin, ".honor setrp 12000");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Hchigh")!.SetUInt32(UpdateFields.PlayerFieldThisWeekKills, 7));
        const string refused = "You have low security level for this.";
        Assert.Equal(refused, await Run(gm, ".honor setrp 0"));
        Assert.Equal(refused, await Run(gm, ".honor reset"));
        Assert.Equal(refused, await Run(gm, ".modify honor thisweekkills 99"));
        Assert.Equal(refused, await Run(gm, ".honor add 100"));

        Assert.Equal((byte)8, await host.PlayerStateAsync("Hchigh", p => p.GetByte(UpdateFields.PlayerBytes3, 3)));
        Assert.Equal(7u, await host.PlayerStateAsync("Hchigh", p => p.GetUInt32(UpdateFields.PlayerFieldThisWeekKills)));
        Assert.Equal(0u, await host.PlayerStateAsync("Hchigh", p => p.GetUInt32(UpdateFields.PlayerFieldThisWeekContribution)));
    }

    [Fact]
    public async Task A_lower_security_gm_may_still_use_the_honor_commands_on_itself_and_on_everyone_when_lower_security_is_off()
    {
        await using WorldTestHost host = StartWithGameMasterAtLevel6(lowerSecurity: false);
        await using WorldTestClient gm = await host.EnterWorldAsync("HCLOWOFF", "Hclowoff", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await host.EnterWorldAsync("HCHIGHOFF", "Hchighoff", AccountSecurity.Administrator);
        await gm.CollectAsync();
        await admin.CollectAsync();
        await SelectAsync(host, "Hclowoff", "Hchighoff");

        Assert.Equal("You have changed rank points of Hchighoff to 12000.", await Run(gm, ".honor setrp 12000"));
        Assert.Equal("The thisweekkills field of Hchighoff was set to 9", await Run(gm, ".modify honor thisweekkills 9"));
    }
}
