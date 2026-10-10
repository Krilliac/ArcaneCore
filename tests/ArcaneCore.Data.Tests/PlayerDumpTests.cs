using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Characters.Dump;
using ArcaneCore.Data.Characters.Items;
using ArcaneCore.Data.Characters.Pets;
using ArcaneCore.Data.Characters.Spells;
using ArcaneCore.Data.Economy;
using ArcaneCore.Data.Honor;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Social;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Social;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>Character export and import (vmangos PlayerDumpWriter / PlayerDumpReader, PlayerDump.cpp) on every engine.</summary>
public sealed class PlayerDumpTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    [Fact]
    public void EveryDumpedTable_IsInTheCharactersModel()
    {
        using CharacterDbContext db = new(new DbContextOptionsBuilder<CharacterDbContext>().UseSqlite("Data Source=:memory:").Options);
        HashSet<string?> tables = [.. db.Model.GetEntityTypes().Select(e => e.GetTableName())];
        Assert.All(PlayerDumpTables.All, t => Assert.Contains(t.Table, tables));
        Assert.Equal(DumpTableType.Character, PlayerDumpTables.All[0].Type);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task RoundTrip_CopiesTheCharacter_WithFreshItemMailTextAndPetIds(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice) = await SeedAsync(provider);
        string dump;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            dump = (await new PlayerDumpWriter(db).GetDumpAsync(alice))!;
            Assert.StartsWith("IMPORTANT NOTE:", dump, StringComparison.Ordinal);
            Assert.Null(await new PlayerDumpWriter(db).GetDumpAsync(9999));
        }

        PlayerDumpLoad load;
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            load = await new PlayerDumpReader(db).LoadDumpAsync(dump, accountId: 3, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db));
        }

        Assert.Equal(DumpReturn.Success, load.Result);
        Assert.Equal("Clone", load.Name);
        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            int clone = load.CharacterId;
            CharacterRecord record = await db.Characters.SingleAsync(c => c.Id == clone);
            Assert.Equal((3, "Clone", (byte)17, 4242u), (record.AccountId, record.Name, record.Level, record.Money));

            Assert.Equal([78u, 6603u], await db.Set<CharacterSpellRow>().Where(r => r.CharacterId == clone).OrderBy(r => r.Spell).Select(r => r.Spell).ToListAsync());
            Assert.Equal(1, await db.Set<HonorCpRow>().CountAsync(r => r.CharacterId == clone));

            // Inventory: new guids, the bag's content points at the new bag, and every item belongs to the clone.
            List<CharacterInventoryRow> inv = await db.Set<CharacterInventoryRow>().Where(r => r.Guid == clone).ToListAsync();
            Assert.Equal(3, inv.Count);
            Assert.DoesNotContain(inv, r => r.ItemGuid is 100 or 101 or 102);
            CharacterInventoryRow bag = inv.Single(r => r.ItemId == 4500);
            Assert.Equal(bag.ItemGuid, inv.Single(r => r.ItemId == 117).Bag);
            List<ItemInstanceRow> items = await db.Set<ItemInstanceRow>().Where(r => r.OwnerGuid == clone).ToListAsync();
            Assert.Equal(4, items.Count); // three carried, one in the mail
            Assert.All(inv, r => Assert.Contains(items, i => i.Guid == r.ItemGuid));

            // The written letter keeps its text under a new id; the mail's item is the clone's new item.
            ItemInstanceRow letter = items.Single(i => i.ItemId == 8383);
            Assert.NotEqual(7u, letter.Text);
            Assert.Equal("dear alice", (await db.Set<ItemTextRow>().SingleAsync(t => t.Id == letter.Text)).Text);
            MailRow mail = await db.Set<MailRow>().SingleAsync(m => m.ReceiverId == clone);
            Assert.NotEqual(50u, mail.Id);
            Assert.Equal("mail body", (await db.Set<ItemTextRow>().SingleAsync(t => t.Id == mail.ItemTextId)).Text);
            Assert.Equal(25u, items.Single(i => i.Guid == mail.ItemGuid).ItemId);
            Assert.Equal(1, await db.Set<ItemLootRow>().CountAsync(r => r.ItemGuid == bag.ItemGuid)); // the bag's loot came along
            Assert.Equal(5u, (await db.Set<ItemLootStateRow>().SingleAsync(r => r.ItemGuid == bag.ItemGuid)).Gold);

            // The pet gets a new number; its cooldown follows it.
            PersistentPetRow pet = await db.Set<PersistentPetRow>().SingleAsync(p => p.CharacterId == clone);
            Assert.NotEqual(900u, pet.PetNumber);
            Assert.Equal(pet.PetNumber, (await db.Set<PersistentPetCooldownRow>().SingleAsync(c => c.CharacterId == clone)).PetNumber);

            // Not dumped: social lists (vmangos dumpTables has no character_social).
            Assert.Empty(await new EfSocialStore(db).GetSocialAsync(clone));

            // The source is untouched, and a dump of the clone carries the same rows.
            Assert.Equal(3, await db.Set<CharacterInventoryRow>().CountAsync(r => r.Guid == alice));
            string again = (await new PlayerDumpWriter(db).GetDumpAsync(clone))!;
            Assert.Equal(Tables(dump), Tables(again));
        }
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_FallsBackToTheDumpsName_AndRefusesWhenThatIsTaken(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice) = await SeedAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        string dump = (await new PlayerDumpWriter(db).GetDumpAsync(alice))!;
        var reader = new PlayerDumpReader(db);

        // "Bob" is taken, so vmangos uses the dump's own name, which is taken too.
        Assert.Equal(DumpReturn.NameInUse, (await reader.LoadDumpAsync(dump, 3, "Bob", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);
        await new EfCharacterStore(db).DeleteAsync(alice, 1);
        PlayerDumpLoad load = await reader.LoadDumpAsync(dump, 3, "", 0, await StoredPlayerDumpIds.CreateAsync(db));
        Assert.Equal((DumpReturn.Success, "Alice"), (load.Result, load.Name));
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_RefusesAFullAccount_AndABrokenDumpLeavesNothing(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice) = await SeedAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        string dump = (await new PlayerDumpWriter(db).GetDumpAsync(alice))!;
        var reader = new PlayerDumpReader(db);
        int before = await db.Characters.CountAsync();

        Assert.Equal(DumpReturn.FileBroken, (await reader.LoadDumpAsync(dump + "no_such_table\t{}\n", 3, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);
        Assert.Equal(DumpReturn.FileBroken, (await reader.LoadDumpAsync(dump.Replace("characters\t", "characters\t{oops", StringComparison.Ordinal), 3, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);

        // A pet cooldown whose pet the dump does not carry breaks the load after the character row was written: rolled back.
        string orphan = string.Join('\n', dump.Split('\n').Where(l => !l.StartsWith("character_pet\t", StringComparison.Ordinal)));
        Assert.Equal(DumpReturn.FileBroken, (await reader.LoadDumpAsync(orphan, 3, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);
        Assert.Equal(DumpReturn.UnexpectedEnd, (await reader.LoadDumpAsync(PlayerDumpWriter.Note + "\n", 3, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);
        Assert.Equal(before, await db.Characters.CountAsync());
        Assert.False(await db.Characters.AnyAsync(c => c.Name == "Clone"));

        for (int i = 0; i < PlayerDumpReader.MaxCharactersPerAccount; i++)
        {
            await new EfCharacterStore(db).CreateAsync(new CharacterRecord { AccountId = 4, Name = "Filler" + (char)('a' + i) });
        }

        Assert.Equal(DumpReturn.TooManyChars, (await reader.LoadDumpAsync(dump, 4, "Clone", 0, await StoredPlayerDumpIds.CreateAsync(db))).Result);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Load_UsesAFreeRequestedId_AndANewOneWhenItIsTaken(DatabaseProvider provider)
    {
        (DatabaseConnectionOptions cs, int alice) = await SeedAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        string dump = (await new PlayerDumpWriter(db).GetDumpAsync(alice))!;
        var reader = new PlayerDumpReader(db);

        PlayerDumpLoad taken = await reader.LoadDumpAsync(dump, 3, "Clone", alice, await StoredPlayerDumpIds.CreateAsync(db));
        Assert.Equal(DumpReturn.Success, taken.Result);
        Assert.NotEqual(alice, taken.CharacterId);

        PlayerDumpLoad free = await reader.LoadDumpAsync(dump, 3, "Clonetwo", 500, await StoredPlayerDumpIds.CreateAsync(db));
        Assert.Equal((DumpReturn.Success, 500), (free.Result, free.CharacterId));
    }

    private static List<string> Tables(string dump)
        => [.. dump.Split('\n').Where(l => l.Contains('\t', StringComparison.Ordinal)).Select(l => l[..l.IndexOf('\t', StringComparison.Ordinal)])];

    private async Task<(DatabaseConnectionOptions, int)> SeedAsync(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(db, CharacterDbContext.Schema);

        var characters = new EfCharacterStore(db);
        int alice = (await characters.CreateAsync(new CharacterRecord { AccountId = 1, Name = "Alice", Level = 17, Money = 4242 })).Id;
        int bob = (await characters.CreateAsync(new CharacterRecord { AccountId = 2, Name = "Bob" })).Id;
        await new EfCharacterSpellStore(db).AddAsync(alice, [78u, 6603u]);
        await new EfSocialStore(db).SetSocialAsync(alice, bob, SocialFlags.Friend);

        // A bag (100) in bag slot 19 with a stack (101) inside it, and a written letter (102) carrying item text 7.
        await new EfItemStore(db).SaveInventoryAsync(alice, new InventorySnapshot(
            [new(0, 19, new ItemInstanceData { Guid = 100, Entry = 4500, Count = 1 }),
             new(100, 0, new ItemInstanceData { Guid = 101, Entry = 117, Count = 4 }),
             new(0, 23, new ItemInstanceData { Guid = 102, Entry = 8383, Count = 1, TextId = 7 })]));
        db.Add(new ItemTextRow { Id = 7, Text = "dear alice" });
        db.Add(new ItemTextRow { Id = 8, Text = "mail body" });
        db.Add(new ItemInstanceRow { Guid = 103, OwnerGuid = alice, ItemId = 25, Count = 1 });
        db.Add(new MailRow { Id = 50, ReceiverId = alice, SenderId = (uint)bob, Subject = "hi", ItemTextId = 8, ItemGuid = 103, ItemEntry = 25 });
        db.Add(new ItemLootStateRow { ItemGuid = 100, Gold = 5 });
        db.Add(new ItemLootRow { ItemGuid = 100, Slot = 0, ItemId = 2589, Amount = 2 });
        db.Add(new PersistentPetRow { CharacterId = alice, PetNumber = 900, Entry = 299, Level = 10, Name = "Wolf" });
        db.Add(new PersistentPetCooldownRow { CharacterId = alice, PetNumber = 900, SpellId = 17253, EndsAtUnixMs = 1 });
        db.Add(new HonorCpRow { CharacterId = alice, VictimId = 1, Cp = 10, Date = 1, Type = 1 });
        await db.SaveChangesAsync();
        return (cs, alice);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();
}
