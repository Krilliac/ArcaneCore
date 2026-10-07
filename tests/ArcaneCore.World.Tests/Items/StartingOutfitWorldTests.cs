using System.Buffers.Binary;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Accounts;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Items;

public sealed class StartingOutfitWorldTests
{
    [Fact]
    public async Task CharacterCreationPersistsDbcOutfitFirstThenSqlItemsAndSkipsMissingTemplate()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-charstartoutfit-{Guid.NewGuid():N}.dbc");
        await File.WriteAllBytesAsync(path, OutfitFile(
            (1, 1, 0, new[] { 25, 500, 999 }),
            (1, 1, 1, new[] { 26, 501 })));
        try
        {
            var content = new ItemTestContent();
            content.Templates.Templates.AddRange([
                new ItemTemplate { Entry = 25, InventoryType = 0 },
                new ItemTemplate { Entry = 26, InventoryType = 0 },
                new ItemTemplate { Entry = 38, InventoryType = 0 },
                new ItemTemplate { Entry = 500, Class = 0, SubClass = 5, Stackable = 20, BuyCount = 1,
                    Spells = [new ItemSpell(1, 0, 0, 0, 0, 11, 0)] },
                new ItemTemplate { Entry = 501, Class = 0, SubClass = 5, Stackable = 20, BuyCount = 1,
                    Spells = [new ItemSpell(1, 0, 0, 0, 0, 59, 0)] },
            ]);
            content.Templates.StartingItems.Add(new StartingItem(1, 1, 38, 1));
            using IDisposable contentScope = content.Use();

            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                IConfiguration configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Items:CharStartOutfitDbcPath"] = path,
                    })
                    .Build();
                services.AddSingleton(configuration);
            });
            await using WorldTestClient male = await CreateLoggedInAsync(host, "OUTFITMALE", "OutfitMale", gender: 0);
            await using WorldTestClient female = await CreateLoggedInAsync(host, "OUTFITFEMALE", "OutfitFemale", gender: 1);

            Account maleAccount = (await host.Accounts.FindByUsernameAsync("OUTFITMALE"))!;
            Account femaleAccount = (await host.Accounts.FindByUsernameAsync("OUTFITFEMALE"))!;
            CharacterRecord maleCharacter = Assert.Single(await host.Characters.GetByAccountAsync(maleAccount.Id));
            CharacterRecord femaleCharacter = Assert.Single(await host.Characters.GetByAccountAsync(femaleAccount.Id));
            Assert.Equal([25u, 500u, 38u], content.Items.Get(maleCharacter.Id).Select(item => item.Item.Entry).ToArray());
            Assert.Equal([4u], content.Items.Get(maleCharacter.Id).Where(item => item.Item.Entry == 500).Select(item => item.Item.Count));
            Assert.Equal([26u, 501u, 38u], content.Items.Get(femaleCharacter.Id).Select(item => item.Item.Entry).ToArray());
            Assert.Equal([2u], content.Items.Get(femaleCharacter.Id).Where(item => item.Item.Entry == 501).Select(item => item.Item.Count));
            Assert.DoesNotContain(content.Items.Get(maleCharacter.Id), item => item.Item.Entry is 26 or 501 or 999);
            Assert.DoesNotContain(content.Items.Get(femaleCharacter.Id), item => item.Item.Entry is 25 or 500 or 999);
            Assert.Equal(2, content.Items.SaveCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<WorldTestClient> CreateLoggedInAsync(WorldTestHost host, string accountName, string characterName, byte gender)
    {
        byte[] key = await host.AddAccountAsync(accountName);
        WorldTestClient client = await host.ConnectAsync();
        await client.AuthenticateAsync(accountName, key);
        await client.CreateCharacterAsync(characterName, race: 1, cls: 1, gender: gender);
        Account account = (await host.Accounts.FindByUsernameAsync(accountName))!;
        CharacterRecord character = Assert.Single(await host.Characters.GetByAccountAsync(account.Id));
        await client.LoginAsync((ulong)character.Id);
        return client;
    }

    private static byte[] OutfitFile(params (byte Race, byte Class, byte Gender, int[] Items)[] rows)
    {
        byte[] bytes = new byte[20 + (rows.Length * 152) + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 152);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        for (int row = 0; row < rows.Length; row++)
        {
            (byte race, byte cls, byte gender, int[] items) = rows[row];
            Span<byte> record = bytes.AsSpan(20 + (row * 152), 152);
            uint packed = race | ((uint)cls << 8) | ((uint)gender << 16);
            BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)(100 + row));
            BinaryPrimitives.WriteUInt32LittleEndian(record[4..], packed);
            for (int i = 0; i < items.Length; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(record[(8 + (i * 4))..], items[i]);
            }
        }

        return bytes;
    }
}
