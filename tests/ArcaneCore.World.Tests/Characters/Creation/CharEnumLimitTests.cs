using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Characters.Creation;

/// <summary>
/// CharactersPerRealm is clamped to 1..10 and the character list shows at most 10, oldest first,
/// without rows whose race/class pair has no create info (vmangos World.cpp:633,
/// CharacterHandler.cpp:168-183, Player.cpp:1632-1650; gtker smsg_char_enum.wowm: the client cannot
/// handle more than 10).
/// </summary>
public sealed class CharEnumLimitTests
{
    private static async Task<int> SeedAsync(WorldTestHost host, string account, int count, params int[] invalidAt)
    {
        int accountId = (await host.Accounts.FindByUsernameAsync(account))!.Id;
        for (int i = 0; i < count; i++)
        {
            await host.Characters.CreateAsync(new CharacterRecord
            {
                AccountId = accountId, Name = "Seed" + (char)('a' + i), Race = (byte)(invalidAt.Contains(i) ? 7 : 1), Class = 1,
            });
        }

        return accountId;
    }

    private static async Task<(byte Count, List<string> Names)> ListAsync(WorldTestClient client)
    {
        await client.SendAsync(WorldOpcode.CmsgCharEnum, []);
        byte[] list = await client.ReadUntilAsync(WorldOpcode.SmsgCharEnum);
        var names = new List<string>();
        int pos = 1;
        for (int i = 0; i < list[0]; i++)
        {
            pos += 8;
            int end = Array.IndexOf(list, (byte)0, pos);
            names.Add(System.Text.Encoding.UTF8.GetString(list, pos, end - pos));
            pos = end + 1 + 9 + 4 + 4 + 12 + 4 + 4 + 1 + 12 + (20 * 5);
        }

        return (list[0], names);
    }

    [Fact]
    public async Task ARealmLimitAboveTen_ActsAsTen_AndTheListShowsTheFirstTen()
    {
        await using var host = WorldTestHost.Start(configure: o => o.CharactersPerRealm = 25);
        byte[] key = await host.AddAccountAsync("LIMIT1");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LIMIT1", key);
        await SeedAsync(host, "LIMIT1", 12);

        Assert.Equal((byte)CharResult.CharCreateServerLimit, await client.TryCreateCharacterAsync("Eleventh"));
        (byte count, List<string> names) = await ListAsync(client);
        Assert.Equal(10, count);
        Assert.Equal("Seeda", names[0]);
        Assert.Equal("Seedj", names[9]);
    }

    [Fact]
    public async Task ARowWithoutCreateInfo_IsNotListed()
    {
        await using var host = WorldTestHost.Start();
        byte[] key = await host.AddAccountAsync("LIMIT2");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LIMIT2", key);
        await SeedAsync(host, "LIMIT2", 3, invalidAt: 1);

        (byte count, List<string> names) = await ListAsync(client);
        Assert.Equal(2, count);
        Assert.Equal(["Seeda", "Seedc"], names);
    }

    [Fact]
    public async Task LegacyMode_ListsEveryRow()
    {
        await using var host = WorldTestHost.Start(
            configure: o => o.CharactersPerRealm = 25,
            configureServices: CreateHandlerOrderTests.Config(("Mode", "Legacy")));
        byte[] key = await host.AddAccountAsync("LIMIT3");
        await using WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync("LIMIT3", key);
        await SeedAsync(host, "LIMIT3", 12, invalidAt: 1);

        Assert.Equal(12, (await ListAsync(client)).Count);
        Assert.Equal((byte)CharResult.CharCreateSuccess, await client.TryCreateCharacterAsync("Eleventh"));
    }
}
