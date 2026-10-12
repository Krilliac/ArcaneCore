using System.Linq.Expressions;
using System.Reflection;
using ArcaneCore.Data.Characters;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Stores;
using ArcaneCore.Kernel.Characters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// The whole characters model, not a hand-picked list: every table that has a column which can hold a character id gets a row for one
/// character, the character is deleted through <c>EfCharacterStore.DeleteAsync</c> (the path
/// CMSG_CHAR_DELETE and the playerbot fleet reset use), and no such table may still hold a row for it. A table added later with a
/// character column is found by the model scan and fails here until its module cleans it up.
/// </summary>
public sealed class CharacterDeletionOrphanTests : IAsyncLifetime
{
    /// <summary>
    /// Columns that hold a character id, by name. The type must be Int32 (the character id type). <c>Guid</c> is the character column of
    /// <c>character_inventory</c> and <c>mail_server_character</c>; in <c>item_instance</c> it is the item guid, which is why that table
    /// is reached by <c>OwnerGuid</c>.
    /// </summary>
    private static readonly HashSet<string> CharacterColumns =
    [
        "CharacterId", "OtherId", "OwnerGuid", "ReceiverId", "SellerId", "BidderId", "LeaderCharacterId", "PlayerId", "OwnerId",
        "LeaderId", "LooterId", "LootOwner", "Guid",
    ];

    /// <summary>
    /// (table, column) pairs that deliberately keep a row after a character is deleted, with why. An entry here is a decision; the test
    /// fails when a table that is not listed keeps a row, and when a listed one stops keeping it (so the list cannot rot).
    /// </summary>
    private static readonly Dictionary<(string Table, string Column), string> Kept = new()
    {
        [("character_group", "LeaderId")] = "the group row is rewritten by the live group sync (GroupManager.OnCharacterDeleted) and a group under two members is dropped at load (GroupDataModule remarks)",
        [("character_group", "LooterId")] = "same as character_group.LeaderId",
        [("loot_state", "LootOwner")] = "dungeon chest state names characters only as inert history and goes with its instance (LootStateDataModule remarks)",
        [("loot_state_player", "CharacterId")] = "same as loot_state.LootOwner",
    };

    private readonly TestDatabases _databases = new();

    public static IEnumerable<object[]> Providers() => TestDatabases.AvailableProviders();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => _databases.DisposeAsync().AsTask();

    [Theory]
    [MemberData(nameof(Providers))]
    public async Task Delete_LeavesNoRowInAnyTableWithACharacterColumn(DatabaseProvider provider)
    {
        DatabaseConnectionOptions cs = await _databases.CreateAsync(provider);
        await using CharacterDbContext setup = TestContexts.Create<CharacterDbContext>(cs);
        await SchemaBootstrapper.EnsureAsync(setup, CharacterDbContext.Schema);
        int target = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord { AccountId = 1, Name = "Orphan" })).Id;
        int bystander = (await new EfCharacterStore(setup).CreateAsync(new CharacterRecord { AccountId = 2, Name = "Bystander" })).Id;

        (IEntityType Entity, IProperty Column)[] pairs = [.. CandidatePairs(setup)];
        Assert.True(pairs.Length > 30, $"the model scan found only {pairs.Length} character columns");

        // One row per (table, column) for the target and one for a bystander, so the delete must remove exactly the target's.
        int serial = 0;
        var notSeeded = new List<string>();
        foreach ((IEntityType entity, IProperty column) in pairs)
        {
            foreach (int owner in new[] { target, bystander })
            {
                if (await TrySeedAsync(cs, entity, column, owner, ++serial) is { } why) notSeeded.Add($"{entity.GetTableName()}.{column.Name}: {why}");
            }
        }

        Assert.True(notSeeded.Count == 0, string.Join(Environment.NewLine, notSeeded.Distinct()));

        await using (CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs))
        {
            Assert.True(await new EfCharacterStore(db).DeleteAsync(target, accountId: 1), "the delete was refused");
        }

        var retained = new List<string>();
        var staleKept = new List<string>();
        await using CharacterDbContext check = TestContexts.Create<CharacterDbContext>(cs);
        foreach ((IEntityType entity, IProperty column) in pairs)
        {
            string key = $"{entity.GetTableName()}.{column.Name}";
            int forTarget = Count(check, entity, column, target);
            int forBystander = Count(check, entity, column, bystander);
            if (Kept.ContainsKey((entity.GetTableName()!, column.Name)))
            {
                if (forTarget == 0) staleKept.Add(key);
            }
            else if (forTarget != 0) retained.Add($"{key} ({forTarget})");

            Assert.True(forBystander >= 1, $"{key}: the delete removed the bystander's row as well");
        }

        Assert.Empty(retained);
        Assert.Empty(staleKept);
    }

    [Fact]
    public void TheCleanedTables_AreListedForTheReport()
    {
        using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(new DatabaseConnectionOptions { Provider = DatabaseProvider.Sqlite, ConnectionString = "Data Source=:memory:" });
        string[] tables = [.. CandidatePairs(db).Select(p => $"{p.Entity.GetTableName()}.{p.Column.Name}").Order(StringComparer.Ordinal)];
        Assert.Contains("character_spell.CharacterId", tables);
        Assert.Contains("character_social.OtherId", tables);
        Assert.Contains("item_instance.OwnerGuid", tables);
    }

    private static IEnumerable<(IEntityType Entity, IProperty Column)> CandidatePairs(DbContext db)
    {
        foreach (IEntityType entity in db.Model.GetEntityTypes().OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
        {
            if (entity.GetTableName() is not { } table || table == "characters") continue;
            foreach (IProperty property in entity.GetProperties())
            {
                // A guild leader cannot be deleted (the module refuses; the fleet reset hands leadership on first), so that column is
                // covered by CharacterDeletionTests, not here.
                if (table == "guild" && property.Name == "LeaderId") continue;
                // A character_deletion row is the pending-deletion marker: the module refuses a character that already has one.
                if (table == "character_deletion") continue;
                if (property.ClrType == typeof(int) && CharacterColumns.Contains(property.Name)) yield return (entity, property);
            }
        }
    }

    /// <summary>Add one row to the entity with the column set to <paramref name="owner"/> and every other key part distinct.</summary>
    private static async Task<string?> TrySeedAsync(DatabaseConnectionOptions cs, IEntityType entity, IProperty column, int owner, int serial)
    {
        try
        {
            object row = Activator.CreateInstance(entity.ClrType, nonPublic: true)!;
            foreach (IProperty property in entity.GetProperties())
            {
                PropertyInfo? clr = property.PropertyInfo;
                if (clr is null || !clr.CanWrite) continue;
                // Key and unique-index parts other than the character column get a value no other row has.
                bool distinct = property.IsPrimaryKey() || property.GetContainingIndexes().Any(i => i.IsUnique);
                object? value = property == column ? owner
                    : property.ClrType == typeof(string) ? (distinct ? "s" + serial.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty)
                    : property.ClrType == typeof(byte[]) ? Array.Empty<byte>()
                    : property.ClrType == typeof(Guid) ? Guid.NewGuid()
                    : distinct && property.ValueGenerated == ValueGenerated.Never && property.ClrType.IsPrimitive && property.ClrType != typeof(bool)
                        ? Convert.ChangeType(serial, property.ClrType, System.Globalization.CultureInfo.InvariantCulture)
                        : null;
                if (value is not null) clr.SetValue(row, value);
            }

            await using CharacterDbContext db = TestContexts.Create<CharacterDbContext>(cs);
            db.Add(row);
            await db.SaveChangesAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ex.GetBaseException().Message;
        }
    }

    private static readonly MethodInfo SetMethod = typeof(DbContext).GetMethods().Single(m => m is { Name: nameof(DbContext.Set), IsGenericMethodDefinition: true } && m.GetParameters().Length == 0);

    private static readonly MethodInfo CountMethod = typeof(Queryable).GetMethods()
        .Single(m => m.Name == nameof(Queryable.Count) && m.GetParameters().Length == 2);

    private static int Count(DbContext db, IEntityType entity, IProperty column, int id)
    {
        object set = SetMethod.MakeGenericMethod(entity.ClrType).Invoke(db, null)!;
        ParameterExpression e = Expression.Parameter(entity.ClrType, "e");
        Expression property = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(int)], e, Expression.Constant(column.Name));
        LambdaExpression predicate = Expression.Lambda(Expression.Equal(property, Expression.Constant(id)), e);
        return (int)CountMethod.MakeGenericMethod(entity.ClrType).Invoke(null, [set, predicate])!;
    }
}
