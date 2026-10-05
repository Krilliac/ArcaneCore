using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// CMSG_CHAR_CREATE over loopback against the retail rules (vmangos CharacterHandler.cpp:185-322):
/// every answer byte and that a refusal stores nothing. The in-memory world data offers human and
/// orc warriors only.
/// </summary>
public sealed class CreateHandlerOrderTests
{
    internal static Action<IServiceCollection> Config(params (string Key, string Value)[] settings) => services =>
    {
        var values = settings.ToDictionary(s => "CharacterCreation:" + s.Key, s => (string?)s.Value);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
    };

    private static async Task<(WorldTestClient Client, int AccountId)> ConnectAsync(
        WorldTestHost host, string account, AccountSecurity security = AccountSecurity.Player)
    {
        byte[] key = await host.AddAccountAsync(account, security);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(account, key);
        return (client, (await host.Accounts.FindByUsernameAsync(account))!.Id);
    }

    [Fact]
    public async Task CharactersCreatingDisabled_RefusesTheTeamForPlayers_NotForStaff()
    {
        await using var host = WorldTestHost.Start(configureServices: Config(("CharactersCreatingDisabled", "1")));
        (WorldTestClient player, int playerId) = await ConnectAsync(host, "DISP");
        await using (player)
        {
            Assert.Equal((byte)CharResult.CharCreateDisabled, await player.TryCreateCharacterAsync("Alliancer", race: 1));
            Assert.Empty(await host.Characters.GetByAccountAsync(playerId));
            Assert.Equal((byte)CharResult.CharCreateSuccess, await player.TryCreateCharacterAsync("Hordeling", race: 2));
        }

        (WorldTestClient staff, _) = await ConnectAsync(host, "DISM", AccountSecurity.Moderator);
        await using (staff)
        {
            Assert.Equal((byte)CharResult.CharCreateSuccess, await staff.TryCreateCharacterAsync("Modally", race: 1));
        }
    }

    [Fact]
    public async Task UnknownRaceIsFailed_NotPlayableRaceIsDisabled_ARealPairWithoutStartRowIsAnError()
    {
        await using var host = WorldTestHost.Start();
        (WorldTestClient client, int id) = await ConnectAsync(host, "RACES");
        await using (client)
        {
            Assert.Equal((byte)CharResult.CharCreateFailed, await client.TryCreateCharacterAsync("Nonrace", race: 12));
            Assert.Equal((byte)CharResult.CharCreateDisabled, await client.TryCreateCharacterAsync("Goblin", race: 9));
            Assert.Equal((byte)CharResult.CharCreateFailed, await client.TryCreateCharacterAsync("Nonclass", race: 1, cls: 6));
            Assert.Equal((byte)CharResult.CharCreateFailed, await client.TryCreateCharacterAsync("Gendery", race: 1, cls: 1, gender: 2));
            Assert.Equal((byte)CharResult.CharCreateError, await client.TryCreateCharacterAsync("Gnomer", race: 7));
            Assert.Empty(await host.Characters.GetByAccountAsync(id));
        }
    }

    [Fact]
    public async Task PvpRealm_OneFactionPerAccount_WithTheDocumentedBypasses()
    {
        await using var host = WorldTestHost.Start(configureServices: Config(("GameType", "PvP")));
        (WorldTestClient player, int playerId) = await ConnectAsync(host, "PVP1");
        await using (player)
        {
            Assert.Equal((byte)CharResult.CharCreateSuccess, await player.TryCreateCharacterAsync("Orcish", race: 2));
            Assert.Equal((byte)CharResult.CharCreatePvpTeamsViolation, await player.TryCreateCharacterAsync("Humanish", race: 1));
            Assert.Single(await host.Characters.GetByAccountAsync(playerId));
        }

        (WorldTestClient gm, _) = await ConnectAsync(host, "PVP2", AccountSecurity.GameMaster);
        await using (gm)
        {
            Assert.Equal((byte)CharResult.CharCreateSuccess, await gm.TryCreateCharacterAsync("Gmorc", race: 2));
            Assert.Equal((byte)CharResult.CharCreateSuccess, await gm.TryCreateCharacterAsync("Gmhuman", race: 1));
        }
    }

    [Fact]
    public async Task AllowTwoSideAccounts_AndNormalRealms_DoNotApplyTheFactionRule()
    {
        await using (var host = WorldTestHost.Start(configureServices: Config(("GameType", "PvP"), ("AllowTwoSideAccounts", "true"))))
        {
            (WorldTestClient c, _) = await ConnectAsync(host, "TWOSIDE");
            await using (c)
            {
                Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Orcish", race: 2));
                Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Humanish", race: 1));
            }
        }

        await using (var host = WorldTestHost.Start())
        {
            (WorldTestClient c, _) = await ConnectAsync(host, "NORMAL");
            await using (c)
            {
                Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Orcish", race: 2));
                Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Humanish", race: 1));
            }
        }
    }

    [Fact]
    public async Task StartLevelAndMoney_AreAppliedToTheNewCharacter()
    {
        await using var host = WorldTestHost.Start(configureServices: Config(
            ("StartPlayerLevel", "5"), ("StartPlayerMoney", "100"), ("GmStartLevel", "8")));
        (WorldTestClient player, int playerId) = await ConnectAsync(host, "START1");
        await using (player)
        {
            await player.CreateCharacterAsync("Levelfive");
            CharacterRecord record = Assert.Single(await host.Characters.GetByAccountAsync(playerId));
            Assert.Equal(((byte)5, 100u), (record.Level, record.Money));

            // The character can still log in with a level other than 1.
            await player.LoginAsync((ulong)record.Id);
        }

        (WorldTestClient gm, int gmId) = await ConnectAsync(host, "START2", AccountSecurity.Moderator);
        await using (gm)
        {
            await gm.CreateCharacterAsync("Leveleight");
            CharacterRecord gmRecord = Assert.Single(await host.Characters.GetByAccountAsync(gmId));
            Assert.Equal(((byte)8, 100u), (gmRecord.Level, gmRecord.Money));
        }
    }

    [Fact]
    public async Task LegacyMode_KeepsTheEarlierPermissiveBehaviour()
    {
        await using var host = WorldTestHost.Start(configureServices: Config(
            ("Mode", "Legacy"), ("CharactersCreatingDisabled", "3"), ("GameType", "PvP"), ("StartPlayerLevel", "5"), ("StartPlayerMoney", "100")));
        (WorldTestClient c, int id) = await ConnectAsync(host, "LEGACY");
        await using (c)
        {
            Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Orcish", race: 2));
            Assert.Equal((byte)CharResult.CharCreateSuccess, await c.TryCreateCharacterAsync("Humanish", race: 1));
            Assert.Equal((byte)CharResult.CharCreateFailed, await c.TryCreateCharacterAsync("Gnomer", race: 7)); // the old "no start row = invalid" answer
            Assert.All(await host.Characters.GetByAccountAsync(id), r => Assert.Equal(((byte)1, 0u), (r.Level, r.Money)));
        }
    }

    [Fact]
    public async Task ALostNameRace_IsAnsweredNameInUse()
    {
        var inner = new InMemoryCharacterStore();
        await using var host = WorldTestHost.Start(configureServices: s => s.AddSingleton<ICharacterStore>(new NameRaceStore(inner)));
        (WorldTestClient c, int id) = await ConnectAsync(host, "RACER");
        await using (c)
        {
            Assert.Equal((byte)CharResult.CharCreateNameInUse, await c.TryCreateCharacterAsync("Thrall"));
            Assert.Empty(await inner.GetByAccountAsync(id));
        }
    }

    /// <summary>Reports the name free and then loses the insert to "another session", as the unique index would.</summary>
    private sealed class NameRaceStore(InMemoryCharacterStore inner) : ICharacterStore
    {
        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => throw new CharacterNameTakenException(character.Name);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, accountId, cancellationToken);
        public Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default) => inner.SaveStateAsync(state, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => inner.GetAllIdentitiesAsync(cancellationToken);
        public Task<IReadOnlyList<int>> FindAccountIdsByNamePrefixAsync(string prefix, int limit, CancellationToken cancellationToken = default) => inner.FindAccountIdsByNamePrefixAsync(prefix, limit, cancellationToken);
    }
}
