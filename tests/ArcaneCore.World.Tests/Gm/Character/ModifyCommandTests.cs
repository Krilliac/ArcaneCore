using ArcaneCore.Game.Entities;
using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Progression;
using ArcaneCore.World.Tests.Talents;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm.Character;

/// <summary>
/// .modify money|hp|mana, .levelup, .replenish and .deplenish (vmangos CharacterCommands.cpp:695-762,
/// 1849-1872, 4460-4523; UnitCommands.cpp:2283-2403; levels Chat.cpp:582-586,1194-1195,1274).
/// Texts: mangos_string 115, 116, 118-121, 127, 153-158, 557-559. Accounts are Administrator so the
/// tests hold under any security map.
/// </summary>
public sealed class ModifyCommandTests
{
    private static string Link(string name) => $"|cffffffff|Hplayer:{name}|h[{name}]|h|r";

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach (string path in new[] { "modify hp", "modify mana", "levelup", "replenish", "deplenish" })   // SEC_GAMEMASTER (3)
        {
            Assert.Null(table.Resolve(path, AccountSecurity.Moderator));
            Assert.NotNull(table.Resolve(path, AccountSecurity.GameMaster));
        }
    }

    [Fact]
    public async Task ModifyMoney_GivesAndTakes_WithTheRetailTexts_AndTellsTheTarget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("MDMGM", "Mdmgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("MDMVIC", "Mdmvic");
        await gm.CollectAsync();
        await victim.CollectAsync();

        // No selection: the caller itself, so no message to a second player.
        Assert.Equal($"You give 500 copper to {Link("Mdmgm")}.", await Run(gm, ".modify money 500"));
        Assert.Equal(500u, await host.PlayerStateAsync("Mdmgm", p => p.Money));

        await SelectAsync(host, "Mdmgm", "Mdmvic");
        Assert.Equal($"You give 700 copper to {Link("Mdmvic")}.", await Run(gm, ".modify money 700"));
        Assert.Equal($"{Link("Mdmgm")} gave you 700 copper.", (await victim.ReadChatAsync()).Text);

        Assert.Equal($"You take 300 copper from {Link("Mdmvic")}.", await Run(gm, ".modify money -300"));
        Assert.Equal($"{Link("Mdmgm")} took 300 copper from you.", (await victim.ReadChatAsync()).Text);
        Assert.Equal(400u, await host.PlayerStateAsync("Mdmvic", p => p.Money));

        Assert.Equal($"You take all copper of {Link("Mdmvic")}.", await Run(gm, ".modify money -999999"));
        Assert.Equal($"{Link("Mdmgm")} took you all of your copper.", (await victim.ReadChatAsync()).Text);
        Assert.Equal(0u, await host.PlayerStateAsync("Mdmvic", p => p.Money));

        // An amount of MAX_MONEY or more sets the purse to the maximum (CharacterCommands.cpp:4513).
        await Run(gm, ".modify money 2147483647");
        Assert.Equal(0x7FFFFFFFu - 1, await host.PlayerStateAsync("Mdmvic", p => p.Money));
    }

    [Fact]
    public async Task ModifyMoney_RejectsMissingAndBadAmounts_WithTheSyntaxLine()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("MDMBAD", "Mdmbad", AccountSecurity.Administrator);
        await gm.CollectAsync();

        Assert.StartsWith("Syntax:", (await Run(gm, ".modify money"))!);
        await gm.CollectAsync();   // the help text has a second line
        Assert.StartsWith("Syntax:", (await Run(gm, ".modify money lots"))!);
        Assert.Equal(0u, await host.PlayerStateAsync("Mdmbad", p => p.Money));
    }

    [Fact]
    public async Task ModifyHpAndMana_SetCurrentAndMaximum_AndRefuseNonPositiveValues()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("MDHGM", "Mdhgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("MDHVIC", "Mdhvic");
        await gm.CollectAsync();
        await victim.CollectAsync();
        await SelectAsync(host, "Mdhgm", "Mdhvic");

        Assert.Equal($"You changed HP of {Link("Mdhvic")} to 100/150.", await Run(gm, ".modify hp 100 150"));
        Assert.Equal($"{Link("Mdhgm")} changed your HP to 100/150.", (await victim.ReadChatAsync()).Text);
        Assert.Equal((100u, 150u), await host.PlayerStateAsync("Mdhvic", p => (p.Health, p.MaxHealth)));

        Assert.Equal($"You changed HP of {Link("Mdhvic")} to 50/50.", await Run(gm, ".modify hp 50"));   // max below current: max = current
        await victim.ReadChatAsync();
        Assert.Equal("Incorrect values.", await Run(gm, ".modify hp 0"));
        Assert.Equal("Incorrect values.", await Run(gm, ".modify hp -5 10"));

        Assert.Equal($"You changed MANA of {Link("Mdhvic")} to 30/40.", await Run(gm, ".modify mana 30 40"));
        Assert.Equal($"{Link("Mdhgm")} changed your MANA to 30/40.", (await victim.ReadChatAsync()).Text);
        Assert.Equal((30u, 40u), await host.PlayerStateAsync("Mdhvic", p => (p.GetUInt32(UpdateFields.UnitFieldPower1), p.GetUInt32(UpdateFields.UnitFieldMaxpower1))));
        Assert.Equal("Incorrect values.", await Run(gm, ".modify mana 0"));
    }

    [Fact]
    public async Task LevelUp_AddsLevels_ClampsToTheRange_ZeroesXp_AndTellsTheTarget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LVLGM", "Lvlgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("LVLVIC", "Lvlvic");
        await gm.CollectAsync();
        await victim.CollectAsync();
        byte max = host.WorldServices.GetRequiredService<ProgressionFeature>().Progression.MaxPlayerLevel;

        // Self: no reply line (the "you changed level" line is for other players only).
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup 4");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLevelupInfo);
        Assert.Equal((byte)5, await host.PlayerStateAsync("Lvlgm", p => p.Level));
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLevelupInfo);
        Assert.Equal((byte)6, await host.PlayerStateAsync("Lvlgm", p => p.Level));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup -100");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLevelupInfo);
        Assert.Equal((byte)1, await host.PlayerStateAsync("Lvlgm", p => p.Level));
        await gm.SendChatAsync(ChatType.Say, Language.Common, ".levelup 250");
        await gm.ReadUntilAsync(WorldOpcode.SmsgLevelupInfo);
        Assert.Equal(max, await host.PlayerStateAsync("Lvlgm", p => p.Level));

        // Another player: ".levelup $name [#levels]" (the name comes first when both are given).
        Assert.Equal($"You changed level of {Link("Lvlvic")} to 4.", await Run(gm, ".levelup lvlvic 3"));
        Assert.Equal($"{Link("Lvlgm")} level up you to (4)", (await victim.ReadChatAsync()).Text);
        Assert.Equal($"You changed level of {Link("Lvlvic")} to 2.", await Run(gm, ".levelup Lvlvic -2"));
        Assert.Equal($"{Link("Lvlgm")} level down you to (2)", (await victim.ReadChatAsync()).Text);
        Assert.Equal($"You changed level of {Link("Lvlvic")} to 2.", await Run(gm, ".levelup Lvlvic 0"));
        Assert.Equal($"{Link("Lvlgm")} reset your level progress.", (await victim.ReadChatAsync()).Text);
        Assert.Equal(0u, await host.PlayerStateAsync("Lvlvic", p => p.GetUInt32(UpdateFields.PlayerXp)));

        // A single argument that is not a number is a name.
        Assert.Equal($"You changed level of {Link("Lvlvic")} to 3.", await Run(gm, ".levelup Lvlvic"));
        Assert.Equal("Player not found!", await Run(gm, ".levelup Nobodyhere"));
        Assert.StartsWith("Syntax:", (await Run(gm, ".levelup 2 Lvlvic"))!);
    }

    /// <summary>
    /// vmangos HandleCharacterLevel: GiveLevel then InitTalentForLevel (CharacterCommands.cpp:1853-1854), so the points follow the
    /// new level both ways, and a level-down below the spent points resets the talents of a non-administrator (Player.cpp:3236-3243).
    /// </summary>
    [Fact]
    public async Task LevelUp_RecomputesTheTalentPoints_AndALevelDownResetsAnOverspend()
    {
        await using WorldTestHost host = TalentResetWorldTests.Start(new TalentWorldFixture());
        await using WorldTestClient gm = await host.EnterWorldAsync("LVTGM", "Lvtgm", AccountSecurity.GameMaster);
        (WorldTestClient victim, _) = await TalentResetWorldTests.EnterAsync(host, "LVTVIC");   // level 12: three points
        await using WorldTestClient victimScope = victim;
        await gm.CollectAsync();

        Assert.Equal($"You changed level of {Link("Lvtvic")} to 14.", await Run(gm, ".levelup Lvtvic 2"));
        Assert.Equal(5u, await host.OnWorldAsync(() => TalentResetWorldTests.FreePoints(host, "LVTVIC")));

        await victim.SendAsync(WorldOpcode.CmsgLearnTalent, TalentResetWorldTests.LearnTalent(1, 2));   // three points spent
        await victim.ReadUntilAsync(WorldOpcode.SmsgLearnedSpell);
        Assert.Equal(2u, await host.OnWorldAsync(() => TalentResetWorldTests.FreePoints(host, "LVTVIC")));

        Assert.Equal($"You changed level of {Link("Lvtvic")} to 10.", await Run(gm, ".levelup Lvtvic -4"));   // one point allowed

        Assert.False(await host.OnWorldAsync(() => TalentResetWorldTests.Knows(host, "LVTVIC", TalentWorldFixture.T1R3)));
        Assert.Equal(1u, await host.OnWorldAsync(() => TalentResetWorldTests.FreePoints(host, "LVTVIC")));
    }

    [Fact]
    public async Task ReplenishAndDeplenish_RefillOrDrainTheSelectionOrTheCaller()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("RPLGM", "Rplgm", AccountSecurity.Administrator);
        await using WorldTestClient victim = await host.EnterWorldAsync("RPLVIC", "Rplvic");
        await gm.CollectAsync();
        await victim.CollectAsync();
        await SelectAsync(host, "Rplgm", "Rplvic");
        await Run(gm, ".modify hp 10 200");

        Assert.Null(await Run(gm, ".replenish", expectReply: false));
        Assert.Equal(200u, await host.PlayerStateAsync("Rplvic", p => p.Health));

        Assert.Null(await Run(gm, ".deplenish", expectReply: false));
        Assert.Equal(1u, await host.PlayerStateAsync("Rplvic", p => p.Health));

        // Nothing selected: the caller itself.
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rplgm")!.Selection = default);
        uint max = await host.PlayerStateAsync("Rplgm", p => p.MaxHealth);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Rplgm")!.Health = 1);
        Assert.Null(await Run(gm, ".replenish", expectReply: false));
        Assert.Equal(max, await host.PlayerStateAsync("Rplgm", p => p.Health));
    }

    private static async Task SelectAsync(WorldTestHost host, string caller, string target)
    {
        Player targetPlayer = await host.PlayerAsync(target);
        await host.OnWorldAsync(() => host.World.FindOnlinePlayer(caller)!.Selection = targetPlayer.Guid);
    }

    private static async Task<string?> Run(WorldTestClient client, string command, bool expectReply = true)
    {
        await client.SendChatAsync(ChatType.Say, Language.Common, command);
        if (expectReply)
        {
            return (await client.ReadChatAsync()).Text;
        }

        await client.SendChatAsync(ChatType.Say, Language.Common, ".nosuchcommand");
        Assert.Equal("There is no such command", (await client.ReadChatAsync()).Text);
        return null;
    }
}
