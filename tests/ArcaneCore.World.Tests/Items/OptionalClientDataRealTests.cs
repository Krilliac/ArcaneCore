using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Game.Items;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Crafting;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Protocol;
using ArcaneCore.World.Characters.Creation;
using ArcaneCore.World.Crafting;
using ArcaneCore.World.Items;
using ArcaneCore.World.Tests.Characters.Creation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.World.Tests.Items;

/// <summary>
/// The optional client data of the live realm, read from the real files: the build-5875 DBCs of <c>ARCANECORE_TEST_DBC_DIR</c> (for example
/// D:\refs\client-dbc-5875-effective) and the classic-db dump of <c>ARCANECORE_CLASSICDB_DUMP</c>, configured with exactly the keys
/// tools\content\set-optional-data.ps1 writes. The world starts without the five "missing optional data" warnings and each feature answers
/// from the data: item set bonuses, random suffixes and their enchantments, readable pages and the character-creation appearance check.
/// </summary>
public sealed class OptionalClientDataRealTests(ITestOutputHelper output)
{
    private const uint RandomSword = 93_510;

    /// <summary>The warnings the live world logged on 2026-10-08 (D:\ArcaneCore-lanes\_deploy\live-w3-r1\world.log) without this data.</summary>
    internal static readonly string[] MissingDataWarnings =
    [
        "ItemSets:DbcPath is not set",
        "ItemRandomProperties:DbcPath and ItemRandomProperties:EnchantmentTemplateDumpPath are not both set",
        "PageText:DumpPath is not set",
        "no SpellItemEnchantment.dbc is configured",
        "appearance (CharSections.dbc)",
        "CharacterCreation:CharSectionsDbcPath",
    ];

    internal static Dictionary<string, string?> Keys(string dbcDirectory, string dump) => new()
    {
        ["ItemSets:DbcPath"] = Path.Combine(dbcDirectory, "ItemSet.dbc"),
        ["ItemRandomProperties:DbcPath"] = Path.Combine(dbcDirectory, "ItemRandomProperties.dbc"),
        ["ItemRandomProperties:EnchantmentTemplateDumpPath"] = dump,
        ["PageText:DumpPath"] = dump,
        ["Enchanting:SpellItemEnchantmentDbcPath"] = Path.Combine(dbcDirectory, "SpellItemEnchantment.dbc"),
        ["CharacterCreation:CharSectionsDbcPath"] = Path.Combine(dbcDirectory, "CharSections.dbc"),
        ["CharacterCreation:CharacterFacialHairStylesDbcPath"] = Path.Combine(dbcDirectory, "CharacterFacialHairStyles.dbc"),
    };

    private static (uint Page, string Text, uint Next) DecodePage(byte[] payload)
    {
        uint page = BinaryPrimitives.ReadUInt32LittleEndian(payload);
        int end = Array.IndexOf(payload, (byte)0, 4);
        return (page, Encoding.UTF8.GetString(payload, 4, end - 4), BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(end + 1)));
    }

    [OptionalClientDataFact]
    public async Task TheLiveRealmsOptionalData_LoadsWithoutWarnings_AndEveryFeatureAnswersFromIt()
    {
        string dbc = Environment.GetEnvironmentVariable(OptionalClientDataFactAttribute.DbcVariable)!;
        string dump = Environment.GetEnvironmentVariable(OptionalClientDataFactAttribute.DumpVariable)!;
        var logs = new LogCapture();
        var content = new ItemTestContent();
        // classic-db item_enchantment_template entry 454: 17 suffixes ("of Intellect" 5, "of Stamina" 15, "of the Owl" 754, ...).
        content.Templates.Templates.Add(new ItemTemplate
        {
            Entry = RandomSword, Class = 2, SubClass = 7, Name = "Random Sword", DisplayId = 1, InventoryType = 13, Delay = 2000, RandomProperty = 454,
        });
        WorldTestHost host;
        using (content.Use())
        {
            host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(Keys(dbc, dump)).Build());
                logs.Register(services);
            });
        }

        await using (host)
        {
            foreach (string warning in MissingDataWarnings)
            {
                Assert.DoesNotContain(logs.Lines, line => line.Contains(warning, StringComparison.Ordinal));
            }

            foreach (string line in logs.Lines.Where(l => l.Contains("Loaded", StringComparison.Ordinal) || l.Contains("Enchanting:", StringComparison.Ordinal)
                || l.Contains("appearance", StringComparison.Ordinal)))
            {
                output.WriteLine(line);
            }

            // Item sets: ItemSet.dbc (patch-2), 172 sets; 209 is "Battlegear of Might" with its 3/5/8-piece bonuses.
            ItemSetCatalog sets = host.WorldServices.GetRequiredService<ItemEquipSpellFeature>().Catalog;
            Assert.Equal(172, sets.Count);
            ItemSetRecord might = sets.Find(209)!;
            Assert.Equal("Battlegear of Might", might.Name);
            Assert.Equal([23562u, 21838u, 23561u], might.SpellIds.Take(3));
            Assert.Equal([3u, 5u, 8u], might.Thresholds.Take(3));

            // Enchantments: SpellItemEnchantment.dbc (patch-2), 1460 rows, so enchanting is active.
            EnchantingFeature enchanting = host.WorldServices.GetRequiredService<EnchantingFeature>();
            Assert.True(enchanting.IsActive);
            Assert.Equal(1460, enchanting.Catalog.Count);

            // Random properties: 2012 suffixes, entry 454 of item_enchantment_template has 17 rows.
            ItemRandomPropertyFeature random = host.WorldServices.GetRequiredService<ItemRandomPropertyFeature>();
            Assert.Equal(2012, random.Properties!.Catalog.PropertyCount);
            IReadOnlyList<ItemEnchantmentChance> group = random.Properties.Catalog.Group(454);
            Assert.Equal(17, group.Count);
            Assert.Equal("of the Owl", random.Properties.Catalog.FindProperty(754)!.Name);

            // Pages: classic-db page_text, 1427 pages.
            Assert.Equal(1427, host.WorldServices.GetRequiredService<PageTextFeature>().Pages.Count);

            // Appearance: CharSections.dbc and CharacterFacialHairStyles.dbc.
            CharacterAppearanceCatalog appearance = host.WorldServices.GetRequiredService<CharacterCreationFeature>().Appearance;
            Assert.Equal((3603, 136), (appearance.SectionCount, appearance.FacialHairStyleCount));

            byte[] key = await host.AddAccountAsync("OPTDATA");
            await using (WorldTestClient creator = await host.ConnectAsync())
            {
                await creator.AuthenticateAsync("OPTDATA", key);
                Assert.Equal((byte)CharResult.CharCreateFailed, await CharacterAppearanceWorldTests.TryCreateAsync(creator, "Oddlooks", new CharacterAppearance(0, 0, 40, 0, 0)));
                Assert.Equal((byte)CharResult.CharCreateFailed, await CharacterAppearanceWorldTests.TryCreateAsync(creator, "Beardlady", new CharacterAppearance(0, 0, 0, 0, 9), gender: 1));
                // Human male, hair style 11 in colour 9 and beard 8: the last of each choice the 1.12.1 client offers.
                Assert.Equal((byte)CharResult.CharCreateSuccess, await CharacterAppearanceWorldTests.TryCreateAsync(creator, "Lastlooks", new CharacterAppearance(0, 0, 11, 9, 8)));
            }

            await using WorldTestClient client = await host.EnterWorldAsync("OPTREAD", "Optreader");

            // A new item of a random_property template rolls one of the group's suffixes and writes its enchantments (slots 3-5), each of which
            // the enchantment catalog resolves to the name the client shows.
            (int property, uint[] enchants) = await host.PlayerStateAsync("Optreader", p =>
            {
                Assert.Equal(InventoryResult.Ok, p.Inventory.AddItem(RandomSword, 1, out Item? item));
                return (item!.RandomPropertyId, new[] { item.EnchantmentId(3), item.EnchantmentId(4), item.EnchantmentId(5) });
            });
            Assert.Contains(group, row => row.EnchantId == (uint)property);
            ItemRandomPropertyRecord rolled = random.Properties.Catalog.FindProperty((uint)property)!;
            Assert.Equal(rolled.EnchantIds.ToArray(), enchants);
            Assert.NotEqual(0u, enchants[0]);
            Assert.All(enchants.Where(id => id != 0), id => Assert.False(string.IsNullOrEmpty(enchanting.Catalog.Find(id)?.Name)));
            output.WriteLine($"rolled property {property} '{rolled.Name}', enchantments {string.Join(", ", enchants.Where(id => id != 0).Select(id => $"{id} '{enchanting.Catalog.Find(id)!.Name}'"))}");

            // Page 15 is the Goldshire note to Morgan (one page).
            byte[] query = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(query, 15);
            await client.SendAsync(WorldOpcode.CmsgPageTextQuery, query);
            (uint page, string text, uint next) = DecodePage(await client.ReadUntilAsync(WorldOpcode.SmsgPageTextQueryResponse));
            Assert.Equal((15u, 0u), (page, next));
            Assert.StartsWith("Hello Morgan,", text, StringComparison.Ordinal);
        }
    }
}

/// <summary>Runs only when both the DBC directory and the classic-db dump are given (see <see cref="OptionalClientDataRealTests"/>).</summary>
public sealed class OptionalClientDataFactAttribute : FactAttribute
{
    public const string DbcVariable = "ARCANECORE_TEST_DBC_DIR";

    public const string DumpVariable = "ARCANECORE_CLASSICDB_DUMP";

    public OptionalClientDataFactAttribute()
    {
        string? dbc = Environment.GetEnvironmentVariable(DbcVariable);
        string? dump = Environment.GetEnvironmentVariable(DumpVariable);
        if (string.IsNullOrWhiteSpace(dbc) || !Directory.Exists(dbc) || string.IsNullOrWhiteSpace(dump) || !File.Exists(dump))
        {
            Skip = $"Set {DbcVariable} to the build-5875 DBC directory and {DumpVariable} to the classic-db z2815 dump to run against the real data.";
        }
    }
}
