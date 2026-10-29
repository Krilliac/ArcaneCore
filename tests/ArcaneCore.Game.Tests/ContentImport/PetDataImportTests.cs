using ArcaneCore.Data.Content;
using ArcaneCore.Data.Content.Import;
using ArcaneCore.Data.Schema;
using ArcaneCore.Data.World.Pets;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ContentImport;

/// <summary>
/// <c>pet_levelstats</c> and <c>petcreateinfo_spell</c> end to end: a classic-db dump goes through <see cref="PetDumpImporter"/> into a real
/// world database, <see cref="EfPetDataStore"/> loads it (as <c>PetsFeature</c> does) and a warlock summons the Imp (creature 416) through the
/// real summon service. The consumers (<c>PetInitializer.InitStatsForLevel</c> and <c>InitCreateSpells</c>) read the content, so a pass shows the
/// imported rows reach the demon. The stat values are invented; the entry and Firebolt (3110) are the retail ids.
/// </summary>
public sealed class PetDataImportTests
{
    private const uint Firebolt = 3110;

    private const string Dump = """
        CREATE TABLE `pet_levelstats` (`creature_entry` mediumint unsigned NOT NULL, `level` tinyint unsigned NOT NULL, `hp` smallint unsigned NOT NULL, `mana` smallint unsigned NOT NULL, `armor` int unsigned NOT NULL DEFAULT '0', `str` smallint unsigned NOT NULL, `agi` smallint unsigned NOT NULL, `sta` smallint unsigned NOT NULL, `inte` smallint unsigned NOT NULL, `spi` smallint unsigned NOT NULL, PRIMARY KEY (`creature_entry`,`level`));
        INSERT INTO `pet_levelstats` VALUES (416,1,42,41,7,22,20,22,23,24),(416,2,55,50,8,23,21,23,24,25);
        CREATE TABLE `petcreateinfo_spell` (`entry` mediumint unsigned NOT NULL DEFAULT '0', `Spell1` mediumint unsigned NOT NULL DEFAULT '0', `Spell2` mediumint unsigned NOT NULL DEFAULT '0', `Spell3` mediumint unsigned NOT NULL DEFAULT '0', `Spell4` mediumint unsigned NOT NULL DEFAULT '0', PRIMARY KEY (`entry`));
        INSERT INTO `petcreateinfo_spell` VALUES (416,3110,0,0,0);
        """;

    private static SpellInfo FireboltSpell() => Spell(Firebolt, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with
    {
        RangeIndex = 4,
        Range = new SpellRange(0, 30),
    };

    private static async Task<PetContent> ImportedContentAsync()
    {
        await using var db = new WorldDbContext(new DbContextOptionsBuilder<WorldDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await SchemaBootstrapper.EnsureAsync(db, WorldDbContext.Schema);
        var importer = new PetDumpImporter();
        importer.Read(new StringReader(Dump));
        await importer.WriteAsync(db, replace: true);
        db.ChangeTracker.Clear();
        return await new EfPetDataStore(db).LoadAsync();
    }

    [Fact]
    public void AnImpSummonedWithoutThePetTables_KeepsItsTemplateStats_AndKnowsNoSpell()
    {
        using var kit = new PetTestKit(extraSpells: [FireboltSpell()], petContent: PetContent.Empty);
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, ImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(ImpEntry, imp.Template.Entry);
        Assert.Equal(100u, imp.MaxHealth);
        Assert.Empty(imp.Summon!.Charm!.SpellStates);
    }

    [Fact]
    public async Task AnImpSummonedAfterTheImport_HasItsLevelStats_AndFirebolt()
    {
        PetContent content = await ImportedContentAsync();
        using var kit = new PetTestKit(extraSpells: [FireboltSpell()], petContent: content);
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, ImpSpell);

        Creature imp = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(ImpEntry, imp.Template.Entry);
        Assert.Equal(1, imp.Level);
        Assert.Equal((42u, 42u), (imp.MaxHealth, imp.Health));
        Assert.Equal((41u, 41u), (imp.GetUInt32(UpdateFields.UnitFieldMaxpower1), imp.GetUInt32(UpdateFields.UnitFieldPower1)));
        Assert.Equal(7u, imp.GetUInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal([22u, 20u, 22u, 23u, 24u], Enumerable.Range(0, 5).Select(i => imp.GetUInt32(UpdateFields.UnitFieldStat0 + i)));
        Assert.Equal([Firebolt], imp.Summon!.Charm!.SpellStates.Select(s => s.Key));
    }
}
