using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using ArcaneCore.World.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Gm;

/// <summary>.pet rename, .pet delete, .pet learnspell and .pet unlearnspell (vmangos CharacterCommands.cpp:5258-5345).</summary>
public sealed class PetGmStoredPetTests
{
    private sealed class MemoryAdminStore : IPetAdminStore
    {
        public Dictionary<uint, StoredPetLocation> Pets { get; } = [];

        public Task<StoredPetLocation?> FindAsync(uint petNumber, CancellationToken cancellationToken = default)
            => Task.FromResult(Pets.GetValueOrDefault(petNumber));

        public Task<bool> RenameAsync(uint petNumber, string name, CancellationToken cancellationToken = default)
        {
            if (!Pets.TryGetValue(petNumber, out StoredPetLocation? pet)) return Task.FromResult(false);
            Pets[petNumber] = pet with { Name = name };
            return Task.FromResult(true);
        }

        public Task<bool> DeleteAsync(uint petNumber, CancellationToken cancellationToken = default) => Task.FromResult(Pets.Remove(petNumber));
    }

    [Fact]
    public void Levels_FollowTheVmangosTable()
    {
        CommandTable table = ChatCommands.CreateTable();
        foreach ((string path, int level) in new[] { ("pet rename", 3), ("pet delete", 3), ("pet learnspell", 5), ("pet unlearnspell", 5) })
        {
            ChatCommand? command = table.Resolve(path, AccountSecurity.Administrator);
            Assert.NotNull(command);
            Assert.Equal(level, command.RequiredLevel(table.Gm));
        }

        Assert.NotNull(table.Resolve("pet rename", AccountSecurity.GameMaster));
        Assert.Null(table.Resolve("pet learnspell", AccountSecurity.GameMaster));
    }

    [Fact]
    public async Task RenameAndDelete_WorkOnOfflineOwners_AndRefuseOnlineOnes()
    {
        var store = new MemoryAdminStore();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IPetAdminStore>(store));
        await using WorldTestClient gm = await host.EnterWorldAsync("PETADMGM", "Petadmgm", AccountSecurity.Administrator);
        int online = (int)(await host.PlayerAsync("Petadmgm")).Guid.Low;
        store.Pets[71] = new StoredPetLocation(71, 9001, "Fang", false);
        store.Pets[72] = new StoredPetLocation(72, 9001, "Claw", true);
        store.Pets[73] = new StoredPetLocation(73, online, "Mine", false);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet rename 71 x");
        Assert.Equal("\"x\" is not a valid pet name", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet rename 70 Bitey");
        Assert.Equal("Pet #70 not found", (await gm.ReadChatAsync()).Text);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet rename 71 Bitey");
        Assert.Equal("Pet #71 (\"Fang\", owner #9001) renamed to \"Bitey\"", (await gm.ReadChatAsync()).Text);
        Assert.Equal("Bitey", store.Pets[71].Name);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet delete 72");
        Assert.Equal("Pet #72 (\"Claw\", owner #9001) deleted.", (await gm.ReadChatAsync()).Text);
        Assert.False(store.Pets.ContainsKey(72));

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet delete 73");
        Assert.Equal("Pet #73 belongs to Petadmgm, who is online; log them out first.", (await gm.ReadChatAsync()).Text);
        Assert.True(store.Pets.ContainsKey(73));
    }

    [Fact]
    public async Task LearnSpell_WithoutAPetSelected_SaysSo()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient gm = await host.EnterWorldAsync("PETADMG2", "Petadmgtwo", AccountSecurity.Administrator);

        await gm.SendChatAsync(ChatType.Say, Language.Common, ".pet learnspell 17253");
        Assert.Equal("Select a pet first.", (await gm.ReadChatAsync()).Text);
    }
}