using ArcaneCore.Game;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using ArcaneCore.World.Gm.Character;
using ArcaneCore.World.Gm.Core;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.pet info, .pet list and .pet loyalty (vmangos CharacterCommands.cpp HandlePet*Command).</summary>
public sealed class PetGmCommandTests
{
    [Fact]
    public void Pet_UsesTheVmangosRetailLevels()
    {
        CommandTable table = ChatCommands.CreateTable();
        Assert.NotNull(table.Resolve("pet info", AccountSecurity.Moderator));
        Assert.Null(table.Resolve("pet list", AccountSecurity.Moderator));
        Assert.NotNull(table.Resolve("pet list", AccountSecurity.GameMaster));
        Assert.NotNull(table.Resolve("pet loyalty", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task InfoAndLoyalty_WithoutAPetSelected_SaySo()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("PETGM1", "Petgmone", AccountSecurity.GameMaster);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet info");
        Assert.Equal(PetGmCommands.NoPetSelected, (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet loyalty 100");
        Assert.Equal(PetGmCommands.NoPetSelected, (await gm.ReadChatAsync()).Text);
    }

    [Fact]
    public async Task List_CountsTheStoredPets_OfAKnownCharacter()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("PETGM2", "Petgmtwo", AccountSecurity.GameMaster);
        uint id = (await host.PlayerAsync("Petgmtwo")).Guid.Low;

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet list Nobodyhere");
        Assert.Equal(GmStrings.PlayerNotFound, (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet list Petgmtwo");
        Assert.Equal($"Found 0 pets for character Petgmtwo (#{id}).", (await gm.ReadChatAsync()).Text);
    }
}