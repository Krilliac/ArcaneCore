using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Talents;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static ArcaneCore.World.Tests.Talents.TalentWorldFixture;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// <c>.reset talents [$name]</c>, <c>.reset all talents</c> and <c>.modify tp #n</c> (vmangos CharacterCommands.cpp:3892-3925,
/// :3969-3990, :4524-4547; levels Chat.cpp:594, :919-921, :1213; texts mangos_string 213, 214, 216, 219) over the synthetic talent
/// catalog of <see cref="TalentWorldFixture"/>. Characters are level 12 (three points).
/// </summary>
public sealed class ResetTalentCommandTests
{
    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    private static WorldTestHost Start(TalentWorldFixture fixture) => TalentResetWorldTests.Start(fixture);

    private static async Task<WorldTestClient> EnterAsync(WorldTestHost host, string name, AccountSecurity security = AccountSecurity.Player)
        => (await TalentResetWorldTests.EnterAsync(host, name, security: security)).Client;

    private static async Task LearnAsync(WorldTestClient client, uint talent, uint rank)
    {
        await client.SendAsync(WorldOpcode.CmsgLearnTalent, TalentResetWorldTests.LearnTalent(talent, rank));
        await client.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
    }

    private static async Task<string?> Run(WorldTestClient client, string command)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        return (await client.ReadChatAsync()).Text;
    }

    /// <summary>An offline character: created on its own account, never logged in.</summary>
    private static async Task<CharacterRecord> CreateOfflineAsync(WorldTestHost host, string name, AccountSecurity security = AccountSecurity.Player)
    {
        byte[] key = await host.AddAccountAsync(name, security);
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(name, key);
        await client.CreateCharacterAsync(name);
        Account account = (await host.Accounts.FindByUsernameAsync(name))!;
        CharacterRecord record = (await host.Characters.GetByAccountAsync(account.Id)).Single();
        record.Level = 12;
        return record;
    }

    private static Task<uint> FreeAsync(WorldTestHost host, string name) => host.OnWorldAsync(() => TalentResetWorldTests.FreePoints(host, name));

    private static Task<bool> KnowsAsync(WorldTestHost host, string name, uint spell) => host.OnWorldAsync(() => TalentResetWorldTests.Knows(host, name, spell));

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();

        Assert.Null(table.Resolve("reset talents", AccountSecurity.Moderator));            // SEC_GAMEMASTER (3)
        Assert.NotNull(table.Resolve("reset talents", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("reset all", AccountSecurity.GameMaster));               // SEC_ADMINISTRATOR (6)
        Assert.NotNull(table.Resolve("reset all", AccountSecurity.Administrator));
        Assert.Null(table.Resolve("modify tp", AccountSecurity.GameMaster));               // SEC_BASIC_ADMIN (4)
        Assert.NotNull(table.Resolve("modify tp", AccountSecurity.Administrator));
    }

    [Fact]
    public async Task ResetTalents_OfAnOnlinePlayer_IsFree_RestoresThePoints_AndTellsBoth()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        await using WorldTestClient gm = await EnterAsync(host, "RTGM", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await EnterAsync(host, "RTVIC");
        await LearnAsync(victim, 1, 2);
        uint money = await host.PlayerStateAsync("RTVIC", p => p.Money);
        Assert.Equal(0u, await FreeAsync(host, "RTVIC"));

        Assert.Equal($"Talents of {Link("Rtvic")} reset.", await Run(gm, ".reset talents Rtvic"));

        Assert.Equal(TalentFeature.TalentsResetText, (await victim.ReadChatAsync()).Text);
        Assert.False(await KnowsAsync(host, "RTVIC", T1R3));
        Assert.Equal(3u, await FreeAsync(host, "RTVIC"));
        Assert.Equal(money, await host.PlayerStateAsync("RTVIC", p => p.Money));
        Assert.Equal(0u, await host.OnWorldAsync(() =>
            host.WorldServices.GetRequiredService<TalentFeature>().Service!.StateOf(host.World.FindOnlinePlayer("RTVIC")!).Respec.Multiplier));
    }

    [Fact]
    public async Task ResetTalents_WithoutAName_TakesTheSelection_OrTheInvoker()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        await using WorldTestClient gm = await EnterAsync(host, "RTSELF", AccountSecurity.GameMaster);
        await using WorldTestClient victim = await EnterAsync(host, "RTSEL");
        await LearnAsync(gm, 1, 0);
        await LearnAsync(victim, 1, 0);

        // Nothing selected: the invoker, who only gets the target's line.
        Assert.Equal(TalentFeature.TalentsResetText, await Run(gm, ".reset talents"));
        Assert.False(await KnowsAsync(host, "RTSELF", T1R1));

        Player selected = await host.PlayerAsync("RTSEL");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("RTSELF")!.Selection = selected.Guid);
        Assert.Equal($"Talents of {Link("Rtsel")} reset.", await Run(gm, ".reset talents"));
        Assert.False(await KnowsAsync(host, "RTSEL", T1R1));
    }

    [Fact]
    public async Task ResetTalents_OfAnOfflineCharacter_IsRequested_AndAppliedAtItsLogin()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await EnterAsync(host, "RTOGM", AccountSecurity.GameMaster);
        CharacterRecord offline = await CreateOfflineAsync(host, "RTOFF");

        Assert.Equal($"Talents of {Link("Rtoff")} will reset at next login.", await Run(gm, ".reset talents Rtoff"));

        Assert.True(fixture.ResetFlags.IsFlagged(offline.Id));
        Assert.Equal("Player not found!", await Run(gm, ".reset talents Nobodyhere"));
    }

    [Fact]
    public async Task ResetTalents_RefusesATargetOfHigherSecurity_OnlineAndOffline()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await EnterAsync(host, "RTLOW", AccountSecurity.GameMaster);
        await using WorldTestClient admin = await EnterAsync(host, "RTHIGH", AccountSecurity.Administrator);
        await LearnAsync(admin, 1, 0);
        CharacterRecord offlineAdmin = await CreateOfflineAsync(host, "RTHIGHOFF", AccountSecurity.Administrator);

        Assert.Equal("You have low security level for this.", await Run(gm, ".reset talents Rthigh"));
        Assert.True(await KnowsAsync(host, "RTHIGH", T1R1));

        Assert.Equal("You have low security level for this.", await Run(gm, ".reset talents Rthighoff"));
        Assert.False(fixture.ResetFlags.IsFlagged(offlineAdmin.Id));
    }

    [Fact]
    public async Task ResetTalents_ReportsAStoreFailure()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient gm = await EnterAsync(host, "RTFAIL", AccountSecurity.GameMaster);
        CharacterRecord offline = await CreateOfflineAsync(host, "RTFAILOFF");
        fixture.ResetFlags.FailWrites = true;

        Assert.Equal(ResetCommands.StoreFailed, await Run(gm, ".reset talents Rtfailoff"));
        Assert.False(fixture.ResetFlags.IsFlagged(offline.Id));
    }

    [Fact]
    public async Task ResetAllTalents_AnnouncesToEveryone_FlagsEveryCharacter_AndAnOnlineResetUsesTheRequestUp()
    {
        var fixture = new TalentWorldFixture();
        await using WorldTestHost host = Start(fixture);
        await using WorldTestClient admin = await EnterAsync(host, "RAADMIN", AccountSecurity.Administrator);
        (WorldTestClient player, CharacterRecord playerRecord) = await TalentResetWorldTests.EnterAsync(host, "RAPLAYER");
        await using WorldTestClient playerScope = player;
        CharacterRecord offline = await CreateOfflineAsync(host, "RAOFF");
        await LearnAsync(player, 1, 0);

        Assert.StartsWith("Syntax: .reset all", await Run(admin, ".reset all spells"));
        await admin.ReadChatAsync();   // the second help line
        Assert.False(fixture.ResetFlags.IsFlagged(offline.Id));

        Assert.Equal(ResetCommands.ResetAllText, await Run(admin, ".reset all talents"));
        Assert.Equal(ResetCommands.ResetAllText, (await player.ReadChatAsync()).Text);
        await host.WaitForWorldAsync(() => fixture.ResetFlags.IsFlagged(offline.Id), "every character is flagged");
        Assert.True(fixture.ResetFlags.IsFlagged(playerRecord.Id));
        Assert.True(await KnowsAsync(host, "RAPLAYER", T1R1));   // online players keep their talents until they log in again

        // A reset while online uses the request up (vmangos ResetTalents clears the flag, Player.cpp:4077-4078).
        Assert.Equal($"Talents of {Link("Raplayer")} reset.", await Run(admin, ".reset talents Raplayer"));
        await host.WaitForWorldAsync(() => !fixture.ResetFlags.IsFlagged(playerRecord.Id), "the online player's request is cleared");
        await player.ReadChatAsync();
        await LearnAsync(player, 1, 0);
        await TalentResetWorldTests.RelogAsync(host, player, playerRecord);
        Assert.True(await KnowsAsync(host, "RAPLAYER", T1R1));
    }

    [Fact]
    public async Task ModifyTp_SetsTheFreePoints_OfTheSelectionOrTheInvoker()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        await using WorldTestClient admin = await EnterAsync(host, "MTPADMIN", AccountSecurity.Administrator);
        await using WorldTestClient victim = await EnterAsync(host, "MTPVIC");

        await admin.SendChatAsync(ChatType.Say, Language.Common, ".modify tp 7");
        await host.WaitForWorldAsync(() => TalentResetWorldTests.FreePoints(host, "MTPADMIN") == 7, "the invoker has 7 points");

        Player target = await host.PlayerAsync("MTPVIC");
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("MTPADMIN")!.Selection = target.Guid);
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".modify tp 0");
        await host.WaitForWorldAsync(() => TalentResetWorldTests.FreePoints(host, "MTPVIC") == 0, "the selection has no points");
        Assert.Equal(7u, await FreeAsync(host, "MTPADMIN"));

        // The granted points are spendable, as in vmangos (LearnTalent reads PLAYER_CHARACTER_POINTS1).
        await admin.SendChatAsync(ChatType.Say, Language.Common, ".modify tp 5");
        await host.WaitForWorldAsync(() => TalentResetWorldTests.FreePoints(host, "MTPVIC") == 5, "the selection has 5 points");
    }

    [Fact]
    public async Task ModifyTp_RejectsMissingNegativeAndNonNumericAmounts()
    {
        await using WorldTestHost host = Start(new TalentWorldFixture());
        await using WorldTestClient admin = await EnterAsync(host, "MTPBAD", AccountSecurity.Administrator);
        await using WorldTestClient other = await EnterAsync(host, "MTPOTHER", AccountSecurity.Administrator);

        Assert.StartsWith("Syntax: .modify tp", await Run(admin, ".modify tp"));
        await admin.ReadChatAsync();   // the second help line
        Assert.StartsWith("Syntax: .modify tp", await Run(admin, ".modify tp -1"));
        await admin.ReadChatAsync();
        Assert.StartsWith("Syntax: .modify tp", await Run(admin, ".modify tp lots"));
        await admin.ReadChatAsync();
        Assert.Equal(3u, await FreeAsync(host, "MTPBAD"));
        Assert.Equal(3u, await FreeAsync(host, "MTPOTHER"));
    }

    [Fact]
    public async Task WithoutTheTalentSystem_TheCommandsSaySo_AndChangeNothing()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient admin = await host.EnterWorldAsync("RTINERT", "Rtinert", AccountSecurity.Administrator);
        await admin.CollectAsync();

        Assert.Equal(ResetCommands.TalentsInert, await Run(admin, ".reset talents"));
        Assert.Equal(ResetCommands.TalentsInert, await Run(admin, ".reset all talents"));
        Assert.Equal(ResetCommands.TalentsInert, await Run(admin, ".modify tp 5"));
        Assert.Equal(0u, await host.PlayerStateAsync("Rtinert", p => p.GetUInt32(UpdateFields.PlayerCharacterPoints1)));
    }
}
