using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Game.Spells;
using ArcaneCore.World.Spells;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.World.Tests.Spells;

/// <summary>
/// <c>spell_script_target.TargetCondition</c> from the database to the store's rows: rows are written to a real (in-memory SQLite) world
/// database, read back by <see cref="EfSpellContentStore.LoadAsync"/> (the EF mapping and the load of <c>SpellWorldDataModule</c>) and handed
/// to <see cref="SpellStoreFactory.Build"/>. The targeting tests build <see cref="SpellStore"/> by hand, so without this a broken column
/// mapping, a dropped load or a dropped factory argument would leave them green. Written for the integration gate; not run in the lane.
/// </summary>
public sealed class SpellStoreFactoryScriptTargetConditionTests
{
    [Fact]
    public async Task ANonEmptyTargetCondition_SurvivesTheDatabaseAndReachesTheStoreRowAndParses()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite(connection).Options;
        SpellContent loaded;
        await using (var db = new WorldDbContext(options))
        {
            await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
            db.Add(new SpellTemplateRow { Id = 990901 });
            db.Add(new SpellScriptTargetRow { SpellId = 990901, Type = 1, TargetEntry = 77, InverseEffectMask = 0, TargetCondition = "health:5:2" });
            db.Add(new SpellScriptTargetRow { SpellId = 990901, Type = 1, TargetEntry = 78, InverseEffectMask = 0 });
            db.Add(new SpellScriptTargetRow { SpellId = 424242, Type = 1, TargetEntry = 79, InverseEffectMask = 0, TargetCondition = "gostate:2" });
            await db.SaveChangesAsync();
        }

        await using (var db = new WorldDbContext(options))
            loaded = await new EfSpellContentStore(db).LoadAsync();

        // The database round trip itself: the column came back, row by row.
        Assert.Equal("health:5:2", Assert.Single(loaded.ScriptTargets, r => r.TargetEntry == 77).TargetCondition);
        Assert.Equal(string.Empty, Assert.Single(loaded.ScriptTargets, r => r.TargetEntry == 78).TargetCondition);
        Assert.Equal("gostate:2", Assert.Single(loaded.ScriptTargets, r => r.TargetEntry == 79).TargetCondition);

        SpellStore store = SpellStoreFactory.Build(loaded);

        IReadOnlyList<SpellStore.ScriptTarget> rows = store.GetScriptTargets(990901);
        Assert.Equal(2, rows.Count);
        SpellStore.ScriptTarget conditional = Assert.Single(rows, r => r.TargetEntry == 77);
        Assert.Equal("health:5:2", conditional.Condition);
        Assert.NotNull(conditional.ParsedCondition);
        SpellStore.ScriptTarget plain = Assert.Single(rows, r => r.TargetEntry == 78);
        Assert.Equal(string.Empty, plain.Condition);
        Assert.Null(plain.ParsedCondition);
        // The row of a spell the store does not know is skipped.
        Assert.Empty(store.GetScriptTargets(424242));
    }
}
