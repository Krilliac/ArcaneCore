using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Tests.Spells;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>
/// The classic <c>.lookup spell</c> contract: ticketmaster visibility, a case-insensitive
/// substring over the resolved spell name, id ordering, clickable spell links, and a no-match
/// response. The catalog is the test SpellStore, never private or production spell data.
/// </summary>
public sealed class LookupSpellCommandsTests
{
    [Fact]
    public void SpellLookup_IsTicketmasterLevel_AndNotModeratorLevel()
    {
        CommandTable table = ChatCommands.CreateTable();

        Assert.NotNull(table.Resolve("lookup spell", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("lookup spell", AccountSecurity.Moderator));
        Assert.Null(table.Resolve("lookup spell", AccountSecurity.Player));
    }

    [Fact]
    public async Task SpellLookup_MatchesNameCaseInsensitively_AndReturnsClickableIdLinks()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LKSPELL1", "Lkspell", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup spell BoLt");
        Assert.Equal("9002 - |cffffffff|Hspell:9002|h[Test Bolt]|h|r", (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task SpellLookup_UsesSyntaxForMissingName_AndNoSpellsFoundForUnknownName()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("LKSPELL2", "Lkspellb", AccountSecurity.GameMaster);
        await gm.CollectAsync();

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup spell");
        Assert.StartsWith("Syntax:", (await gm.ReadChatAsync()).Text);
        Assert.Equal("Looks up a spell by name.", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".lookup spell definitely-not-a-spell");
        Assert.Equal("No spells found!", (await gm.ReadChatAsync()).Text);
    }
}
