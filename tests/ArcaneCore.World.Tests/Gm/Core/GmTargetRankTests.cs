using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Gm.Core;
using ArcaneCore.World.Tests.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Core;

/// <summary>
/// Every GM command that changes another player applies the target-rank check
/// (<see cref="ArcaneCore.World.Commands.CommandContext.CanActOn"/>, the vmangos HasLowerSecurity rule,
/// Chat.cpp:1521-1563): a GameMaster selecting or naming an Administrator is refused with
/// "You have low security level for this." and the Administrator is left untouched. The shipped
/// <see cref="GmOptions.LowerSecurity"/> default (on) is what makes a staff caller subject to it.
/// </summary>
public sealed class GmTargetRankTests
{
    private const uint Jerky = 117;
    private const string Boss = "Rankboss";
    private const string Gm = "Rankgm";

    /// <summary>
    /// The commands a GameMaster may run that change the selected (or named) player. The first block had no
    /// target-rank check (Codex security finding S3); the second already had one and is kept here so the sweep
    /// covers every target-changing handler a GameMaster can reach.
    /// </summary>
    public static readonly TheoryData<string> TargetChangingCommands =
    [
        // Newly guarded.
        ".additem 117 1",
        ".additem 117 -1",
        ".deleteitem 117 1",
        ".deleteitem 117 1 Rankboss",
        ".levelup 1",
        ".levelup Rankboss 1",
        ".levelup Rankboss",
        ".replenish",
        ".deplenish",
        ".revive",
        ".revive Rankboss",
        ".explorecheat 1",
        ".explorecheat 0",
        ".showarea 12",
        ".hidearea 12",

        // Already guarded (regression).
        ".modify hp 5",
        ".modify mana 5",
        ".modify energy 5",
        ".modify rage 5",
        ".kick Rankboss",
        ".namego Rankboss",
        ".recall Rankboss",
        ".unlearn 133",
        ".unaura all",
        ".maxskill",
        ".repairitems",
        ".character rename Rankboss",
    ];

    [Theory]
    [MemberData(nameof(TargetChangingCommands))]
    public async Task LowerRankGm_IsRefused_AndTheHigherRankTargetIsUnchanged(string command)
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await boss.CollectAsync();
        await gm.CollectAsync();

        // Below full health, so .replenish has something to restore and .revive something to change.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(Boss)!.Health = 10);
        await SelectAsync(host, Gm, Boss);
        uint[] before = await SnapshotAsync(host, Boss);

        List<string> replies = await RunAsync(gm, command);

        Assert.Equal([GmStrings.SecurityTooLow], replies);
        Assert.Equal(before, await SnapshotAsync(host, Boss));
        Assert.NotNull(await host.OnWorldAsync(() => host.World.FindOnlinePlayer(Boss)));   // .kick did not disconnect it
    }

    /// <summary>The control: the same commands from the higher rank to the lower one are not refused.</summary>
    [Theory]
    [MemberData(nameof(TargetChangingCommands))]
    public async Task HigherRankGm_IsNotRefused_OnALowerRankTarget(string command)
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await boss.CollectAsync();
        await gm.CollectAsync();
        await SelectAsync(host, Boss, Gm);

        List<string> replies = await RunAsync(boss, command.Replace(Boss, Gm, StringComparison.Ordinal));

        Assert.DoesNotContain(GmStrings.SecurityTooLow, replies);
    }

    [Fact]
    public async Task GuildAdmin_RefusesAHigherRankOnlineCharacter_ByNameOrSelection()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await using WorldTestClient member = await host.EnterWorldAsync("RANKMEMBER", "Rankmember");
        await boss.CollectAsync();
        await gm.CollectAsync();
        await member.CollectAsync();
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(Gm)!.Selection = default);

        Assert.Equal(["Guild Minions created."], await RunAsync(gm, ".guild create Rankmember \"Minions\""));

        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, $".guild create {Boss} \"Coup\""));
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, $".guild invite {Boss} \"Minions\""));
        Assert.Equal(0u, await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildid)));

        Assert.Equal(["Guild Bosses created."], await RunAsync(boss, $".guild create {Boss} \"Bosses\""));
        uint bosses = await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.NotEqual(0u, bosses);
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, $".guild rank {Boss} 4"));
        await SelectAsync(host, Gm, Boss);
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, ".guild uninvite"));
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, ".guild rank 4"));
        Assert.Equal(bosses, await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildid)));
        Assert.Equal(0u, await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildrank)));
    }

    [Fact]
    public async Task GuildAdmin_ReadsTheOwnerAccountOfAnOfflineCharacter_RefusingAHigherRankOne()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await using WorldTestClient member = await host.EnterWorldAsync("RANKMEMBER", "Rankmember");
        await OfflineCharacterAsync(host, "RANKOFFBOSS", "Rankoffboss", AccountSecurity.Administrator);
        await OfflineCharacterAsync(host, "RANKOFFPLAYER", "Rankoffplyr", AccountSecurity.Player);
        await boss.CollectAsync();
        await gm.CollectAsync();
        await member.CollectAsync();

        Assert.Equal(["Guild Minions created."], await RunAsync(gm, ".guild create Rankmember \"Minions\""));

        // The owner account's security decides (looked up off the world thread, so the answer may come late).
        Assert.Equal(GmStrings.SecurityTooLow, await FirstReplyAsync(gm, ".guild invite Rankoffboss \"Minions\""));
        Assert.Equal(GmStrings.SecurityTooLow, await FirstReplyAsync(gm, ".guild create Rankoffboss \"Coup\""));
        Assert.Equal("Added to Minions.", await FirstReplyAsync(gm, ".guild invite Rankoffplyr \"Minions\""));
        Assert.Equal("Rank set to 3.", await FirstReplyAsync(gm, ".guild rank Rankoffplyr 3"));
        Assert.Equal("Removed from the guild.", await FirstReplyAsync(gm, ".guild uninvite Rankoffplyr"));
        Assert.Equal(["No guild with that name."], await RunAsync(gm, ".guild invite Rankmember \"Coup\""));

        // The administrator is served everywhere, without the offline lookup.
        Assert.Equal(["Added to Minions."], await RunAsync(boss, ".guild invite Rankoffboss \"Minions\""));
    }

    /// <summary>
    /// Under the shipped levels <c>.guild delete</c> is SEC_BASIC_ADMIN (retail 4, Chat.cpp:449), above a GameMaster
    /// (retail 3), so the rank question below only arises once the levels are switched off or remapped.
    /// </summary>
    [Fact]
    public async Task GuildDelete_IsNotAvailableToAGameMaster_UnderTheShippedLevels()
    {
        await using WorldTestHost host = Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await gm.CollectAsync();

        Assert.Equal(["Guild Minions created."], await RunAsync(gm, $".guild create {Gm} \"Minions\""));
        Assert.Equal(["This command is not available to you."], await RunAsync(gm, ".guild delete \"Minions\""));
        Assert.NotEqual(0u, await host.PlayerStateAsync(Gm, p => p.GetUInt32(UpdateFields.PlayerGuildid)));
    }

    /// <summary>
    /// Disbanding removes every member, so <c>.guild delete</c> needs the target-rank check against each of them: a
    /// GameMaster (who can reach the command with <c>World:GmCommands:RetailLevels</c> off) may not disband a guild an
    /// online Administrator leads or belongs to.
    /// </summary>
    [Fact]
    public async Task GuildDelete_RefusesAGuildWithAHigherRankOnlineMember()
    {
        await using WorldTestHost host = Start(DeleteReachable);
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await using WorldTestClient member = await host.EnterWorldAsync("RANKMEMBER", "Rankmember");
        await boss.CollectAsync();
        await gm.CollectAsync();
        await member.CollectAsync();

        // Led by the administrator.
        Assert.Equal(["Guild Bosses created."], await RunAsync(boss, $".guild create {Boss} \"Bosses\""));
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, ".guild delete \"Bosses\""));
        Assert.NotEqual(0u, await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildid)));
        Assert.Equal(["Removed from the guild."], await RunAsync(boss, $".guild uninvite {Boss}"));

        // The administrator only a member.
        Assert.Equal(["Guild Minions created."], await RunAsync(gm, ".guild create Rankmember \"Minions\""));
        Assert.Equal(["Added to Minions."], await RunAsync(boss, $".guild invite {Boss} \"Minions\""));
        Assert.Equal([GmStrings.SecurityTooLow], await RunAsync(gm, ".guild delete \"Minions\""));
        uint minions = await host.PlayerStateAsync(Boss, p => p.GetUInt32(UpdateFields.PlayerGuildid));
        Assert.NotEqual(0u, minions);
        Assert.Equal(minions, await host.PlayerStateAsync("Rankmember", p => p.GetUInt32(UpdateFields.PlayerGuildid)));

        // Once no member outranks the GameMaster (itself included), it is served; the administrator always is.
        Assert.Equal(["Removed from the guild."], await RunAsync(boss, $".guild uninvite {Boss}"));
        Assert.Equal(["Added to Minions."], await RunAsync(gm, $".guild invite {Gm} \"Minions\""));
        Assert.Equal(["Guild Minions deleted."], await RunAsync(gm, ".guild delete \"Minions\""));
        Assert.Equal(0u, await host.PlayerStateAsync("Rankmember", p => p.GetUInt32(UpdateFields.PlayerGuildid)));
        Assert.Equal(["Guild Staff created."], await RunAsync(gm, $".guild create {Gm} \"Staff\""));
        Assert.Equal(["Guild Staff deleted."], await RunAsync(boss, ".guild delete \"Staff\""));
        Assert.Equal(["No guild with that name."], await RunAsync(gm, ".guild delete \"Staff\""));
    }

    /// <summary>
    /// <c>.guild delete</c> on a guild with an offline member reads that member's owner account first (off the world
    /// thread, so the answer may come late): an offline Administrator blocks a GameMaster, an offline Player does not.
    /// </summary>
    [Fact]
    public async Task GuildDelete_ReadsTheOwnerAccountOfEachOfflineMember()
    {
        await using WorldTestHost host = Start(DeleteReachable);
        await using WorldTestClient boss = await host.EnterWorldAsync("RANKBOSS", Boss, AccountSecurity.Administrator);
        await using WorldTestClient gm = await host.EnterWorldAsync("RANKGM", Gm, AccountSecurity.GameMaster);
        await using WorldTestClient member = await host.EnterWorldAsync("RANKMEMBER", "Rankmember");
        await OfflineCharacterAsync(host, "RANKOFFBOSS", "Rankoffboss", AccountSecurity.Administrator);
        await OfflineCharacterAsync(host, "RANKOFFPLAYER", "Rankoffplyr", AccountSecurity.Player);
        await boss.CollectAsync();
        await gm.CollectAsync();
        await member.CollectAsync();

        Assert.Equal(["Guild Minions created."], await RunAsync(gm, ".guild create Rankmember \"Minions\""));
        Assert.Equal(["Added to Minions."], await RunAsync(boss, ".guild invite Rankoffboss \"Minions\""));
        Assert.Equal(GmStrings.SecurityTooLow, await FirstReplyAsync(gm, ".guild delete \"Minions\""));
        Assert.NotEqual(0u, await host.PlayerStateAsync("Rankmember", p => p.GetUInt32(UpdateFields.PlayerGuildid)));
        Assert.Equal(["Rankoffboss's guild: Minions."], await GuildOfAsync(host, "Rankoffboss"));

        Assert.Equal("Guild Plebs created.", await FirstReplyAsync(gm, ".guild create Rankoffplyr \"Plebs\""));
        Assert.Equal("Guild Plebs deleted.", await FirstReplyAsync(gm, ".guild delete \"Plebs\""));
        Assert.Empty(await GuildOfAsync(host, "Rankoffplyr"));

        // The administrator needs no lookup.
        Assert.Equal(["Guild Minions deleted."], await RunAsync(boss, ".guild delete \"Minions\""));
        Assert.Empty(await GuildOfAsync(host, "Rankoffboss"));
    }

    private static async Task<string> FirstReplyAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync();
            if (message.Type == ChatType.System)
            {
                return message.Text;
            }
        }
    }

    private static async Task OfflineCharacterAsync(WorldTestHost host, string account, string name, AccountSecurity security)
    {
        byte[] key = await host.AddAccountAsync(account, security);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        await client.CreateCharacterAsync(name);
    }

    private static Task<uint[]> SnapshotAsync(WorldTestHost host, string name) => host.PlayerStateAsync(name, p =>
    {
        var fields = new List<uint>
        {
            p.Inventory.GetItemCount(Jerky, inBankAlso: true),
            p.GetUInt32(UpdateFields.UnitFieldLevel),
            p.GetUInt32(UpdateFields.PlayerXp),
            p.GetUInt32(UpdateFields.UnitFieldHealth),
            p.GetUInt32(UpdateFields.UnitFieldMaxhealth),
            p.GetUInt32(UpdateFields.PlayerFieldCoinage),
            p.GetUInt32(UpdateFields.PlayerGuildid),
            p.MapId,
            BitConverter.SingleToUInt32Bits(p.X),
            BitConverter.SingleToUInt32Bits(p.Y),
        };
        for (int i = 0; i < 5; i++)
        {
            fields.Add(p.GetUInt32(UpdateFields.UnitFieldPower1 + i));
            fields.Add(p.GetUInt32(UpdateFields.UnitFieldMaxpower1 + i));
        }

        for (int i = 0; i < 64; i++)
        {
            fields.Add(p.GetUInt32(UpdateFields.PlayerExploredZones1 + i));
        }

        return fields.ToArray();
    });

    private static async Task SelectAsync(WorldTestHost host, string caller, string target)
    {
        Player targetPlayer = await host.PlayerAsync(target);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(caller)!.Selection = targetPlayer.Guid);
    }

    /// <summary>Send a command and return every system line it produced (a sentinel command marks the end).</summary>
    private static async Task<List<string>> RunAsync(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        await client.SendChatAsync(ChatType.Say, Language.Common, ".nosuchcommand");
        var replies = new List<string>();
        while (true)
        {
            ChatMessage message = await client.ReadChatAsync();
            if (message.Type != ChatType.System)
            {
                continue;
            }

            if (message.Text == "There is no such command")
            {
                return replies;
            }

            replies.Add(message.Text);
        }
    }

    /// <summary>The four-level declarations, under which a GameMaster reaches <c>.guild delete</c>.</summary>
    private static readonly Dictionary<string, string?> DeleteReachable = new() { ["World:GmCommands:RetailLevels"] = "false" };

    /// <summary>The guild an offline (or online) character belongs to, read from the guild manager on the world thread.</summary>
    private static Task<string[]> GuildOfAsync(WorldTestHost host, string name) => host.OnWorldAsync(() =>
    {
        ArcaneCore.Game.Social.SocialContext social = host.WorldServices.GetRequiredService<ArcaneCore.World.Social.SocialFeature>().Context;
        return social.Characters.FindByName(name) is { } info && social.Guilds.GetGuildOf(info.Id) is { } guild
            ? new[] { $"{name}'s guild: {guild.Name}." }
            : [];
    });

    private static WorldTestHost Start(Dictionary<string, string?>? configuration = null)
    {
        var content = new ItemTestContent();
        content.Templates.Templates.AddRange(
        [
            new ItemTemplate { Entry = 38, Class = 4, SubClass = 0, Name = "Recruit's Shirt", DisplayId = 9891, Quality = 1, InventoryType = 4 },
            new ItemTemplate { Entry = Jerky, Class = 0, SubClass = 0, Name = "Tough Jerky", DisplayId = 2473, Quality = 1, Stackable = 20 },
        ]);
        content.Templates.StartingItems.AddRange([new StartingItem(1, 1, 38, 1), new StartingItem(1, 1, Jerky, 4)]);
        using (content.Use())
        {
            return WorldTestHost.Start(configureServices: configuration is null ? null : services =>
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(configuration).Build()));
        }
    }
}
