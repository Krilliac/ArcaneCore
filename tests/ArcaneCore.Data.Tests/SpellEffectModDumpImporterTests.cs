using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>
/// <see cref="SpellEffectModDumpImporter"/>: vmangos <c>spell_effect_mod</c> rows (vmangos mangos.sql layout: every effect column defaults to
/// -1, "keep the spell's value") into the world's <c>spell_effect_mod</c>, reviewed spells only.
/// </summary>
public sealed class SpellEffectModDumpImporterTests
{
    /// <summary>
    /// vmangos rows as the dump has them: 16468 Mother's Milk (reviewed) and 23881 Bloodthirst rank 1, whose heal trigger ArcaneCore's
    /// warrior scripts already implement (not reviewed). The second 16468 row names only two columns, the rest keeping the -1 default.
    /// </summary>
    private const string Dump = """
        INSERT INTO `spell_effect_mod` (`Id`,`EffectIndex`,`Effect`,`EffectDieSides`,`EffectBaseDice`,`EffectDicePerLevel`,`EffectRealPointsPerLevel`,`EffectBasePoints`,`EffectAmplitude`,`EffectPointsPerComboPoint`,`EffectChainTarget`,`EffectMultipleValue`,`EffectMechanic`,`EffectImplicitTargetA`,`EffectImplicitTargetB`,`EffectRadiusIndex`,`EffectApplyAuraName`,`EffectItemType`,`EffectMiscValue`,`EffectTriggerSpell`,`Comment`) VALUES
        (16468,1,6,1,1,0,0,0,11999,0,0,0,0,6,0,0,23,0,1,15474,'Mothers Milk'),
        (23881,1,64,-1,-1,-1,-1,-1,-1,-1,-1,-1,0,1,-1,-1,0,-1,0,23885,'Bloodthirst');
        INSERT INTO `spell_effect_mod` (`Id`,`EffectIndex`,`EffectApplyAuraName`) VALUES
        (16468,2,4);
        """;

    [Fact]
    public void Read_KeepsTheReviewedSpellsRows_AndNamesTheOthers()
    {
        var importer = new SpellEffectModDumpImporter();
        importer.Read(new StringReader(Dump));

        Assert.True(importer.HasRows);
        Assert.Equal([(16468u, (byte)1), (16468u, (byte)2)], importer.Rows.Select(r => (r.Id, r.EffectIndex)));
        Assert.Equal([23881u], importer.NotReviewed);
        Assert.Contains("23881", importer.Describe(), StringComparison.Ordinal);

        SpellEffectModRow milk = importer.Rows.First();
        Assert.Equal((6, 23, 6, 11999, 15474, 1, 0L), (milk.Effect, milk.EffectApplyAuraName, milk.EffectImplicitTargetA, milk.EffectAmplitude,
            milk.EffectTriggerSpell, milk.EffectMiscValue, milk.EffectItemType));

        // A column the INSERT does not name keeps the table's -1 default: the spell's own value stays.
        SpellEffectModRow partial = importer.Rows.Last();
        Assert.Equal((-1, 4, -1, -1L, -1f), (partial.Effect, partial.EffectApplyAuraName, partial.EffectTriggerSpell, partial.EffectItemType, partial.EffectDicePerLevel));
    }

    [Fact]
    public void ReviewedSpells_IsMothersMilkOnly()
        => Assert.Equal([16468u], SpellEffectModDumpImporter.ReviewedSpells.Order());

    [Fact]
    public async Task ReplaceAsync_WritesTheRows_AndTheSpellStoreLoadsThem()
    {
        string path = Path.Combine(Path.GetTempPath(), $"arcane-effectmod-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<WorldDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options;
            await using (var db = new WorldDbContext(options))
            {
                await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
                db.Set<SpellEffectModRow>().Add(new SpellEffectModRow { Id = 1, EffectIndex = 0 }); // replaced, not kept
                await db.SaveChangesAsync();
                var importer = new SpellEffectModDumpImporter();
                importer.Read(new StringReader(Dump));
                await importer.ReplaceAsync(db, CancellationToken.None);
            }

            await using var read = new WorldDbContext(options);
            SpellContent content = await new EfSpellContentStore(read).LoadAsync();
            Assert.Equal([(16468u, (byte)1, 15474), (16468u, (byte)2, -1)],
                content.EffectMods.OrderBy(m => m.EffectIndex).Select(m => (m.Id, m.EffectIndex, m.EffectTriggerSpell)));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
