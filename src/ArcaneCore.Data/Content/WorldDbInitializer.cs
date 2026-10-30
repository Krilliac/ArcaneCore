using ArcaneCore.Data.Schema;
using ArcaneCore.Data.Schema.Upgrade;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ArcaneCore.Data.Content;

/// <summary>
/// Brings the world-content schema to the current version and seeds the M3 world data (start
/// positions, race appearance/faction, class base stats) into empty tables. The content
/// importer (M8) replaces these seeds with imported data.
/// </summary>
public sealed class WorldDbInitializer(IServiceProvider services, ILogger<WorldDbInitializer> logger)
{
    // race => (map, zone, x, y, z, orientation)
    private static readonly (byte Race, uint Map, uint Zone, float X, float Y, float Z, float O)[] StartPositions =
    [
        (1, 0, 12, -8949.95f, -132.493f, 83.5312f, 0f),       // Human — Northshire
        (2, 1, 14, -618.518f, -4251.67f, 38.718f, 0f),        // Orc — Valley of Trials
        (3, 0, 1, -6240.32f, 331.033f, 382.758f, 6.17716f),   // Dwarf — Coldridge Valley
        (4, 1, 141, 10311.3f, 832.463f, 1326.41f, 5.69632f),  // Night Elf — Shadowglen
        (5, 0, 85, 1676.71f, 1678.31f, 121.67f, 2.70526f),    // Undead — Deathknell
        (6, 1, 215, -2917.58f, -257.98f, 52.9968f, 0f),       // Tauren — Camp Narache
        (7, 0, 1, -6240.32f, 331.033f, 382.758f, 6.17716f),   // Gnome — Coldridge Valley
        (8, 1, 14, -618.518f, -4251.67f, 38.718f, 0f),        // Troll — Valley of Trials
    ];

    // race => valid class ids (vanilla 1.12.1)
    private static readonly (byte Race, byte[] Classes)[] RaceClasses =
    [
        (1, [1, 2, 4, 5, 8, 9]),    // Human
        (2, [1, 3, 4, 7, 9]),       // Orc
        (3, [1, 2, 3, 4, 5]),       // Dwarf
        (4, [1, 3, 4, 5, 11]),      // Night Elf
        (5, [1, 4, 5, 8, 9]),       // Undead
        (6, [1, 3, 7, 11]),         // Tauren
        (7, [1, 4, 8, 9]),          // Gnome
        (8, [1, 3, 4, 5, 7, 8]),    // Troll
    ];

    // race => (male display, female display, faction template)
    private static readonly (byte Race, uint Male, uint Female, uint Faction)[] RaceDisplays =
    [
        (1, 49, 50, 1), (2, 51, 52, 2), (3, 53, 54, 3), (4, 55, 56, 4),
        (5, 57, 58, 5), (6, 59, 60, 6), (7, 1563, 1564, 115), (8, 1478, 1479, 116),
    ];

    // class => (base health, base mana, power type: 0 mana, 1 rage, 3 energy)
    private static readonly (byte Class, uint Health, uint Mana, byte Power)[] ClassStats =
    [
        (1, 60, 0, 1),    // Warrior — rage
        (2, 50, 80, 0),   // Paladin
        (3, 50, 70, 0),   // Hunter
        (4, 50, 0, 3),    // Rogue — energy
        (5, 40, 100, 0),  // Priest
        (7, 50, 75, 0),   // Shaman
        (8, 40, 100, 0),  // Mage
        (9, 45, 90, 0),   // Warlock
        (11, 45, 80, 0),  // Druid
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using IServiceScope scope = services.CreateScope();
        WorldDbContext db = scope.ServiceProvider.GetRequiredService<WorldDbContext>();
        SchemaUpgradeOptions options = DatabaseStartup.OptionsFrom(services);

        await SchemaBootstrapper.EnsureAsync(
            db,
            WorldDbContext.Schema,
            options,
            logger,
            cancellationToken,
            async ct =>
            {
                bool added = false;
                if (!await db.PlayerCreateInfo.AnyAsync(ct).ConfigureAwait(false))
                {
                    var positions = StartPositions.ToDictionary(p => p.Race);
                    foreach ((byte race, byte[] classes) in RaceClasses)
                    {
                        var pos = positions[race];
                        foreach (byte cls in classes)
                        {
                            db.PlayerCreateInfo.Add(new PlayerCreateInfoRow
                            {
                                Race = race,
                                Class = cls,
                                MapId = pos.Map,
                                ZoneId = pos.Zone,
                                X = pos.X,
                                Y = pos.Y,
                                Z = pos.Z,
                                Orientation = pos.O,
                            });
                        }
                    }

                    added = true;
                }

                if (!await db.RaceInfo.AnyAsync(ct).ConfigureAwait(false))
                {
                    foreach ((byte race, uint male, uint female, uint faction) in RaceDisplays)
                    {
                        db.RaceInfo.Add(new RaceInfoRow { Race = race, Gender = 0, DisplayId = male, FactionTemplate = faction });
                        db.RaceInfo.Add(new RaceInfoRow { Race = race, Gender = 1, DisplayId = female, FactionTemplate = faction });
                    }

                    added = true;
                }

                if (!await db.ClassInfo.AnyAsync(ct).ConfigureAwait(false))
                {
                    foreach ((byte cls, uint health, uint mana, byte power) in ClassStats)
                    {
                        db.ClassInfo.Add(new ClassInfoRow { Class = cls, BaseHealth = health, BaseMana = mana, PowerType = power });
                    }

                    added = true;
                }

                // The vmangos 5875 Warden scans (World.Warden.WardenCheckSeed), into an empty table only: a table an admin edited is left alone.
                if (!await db.Set<World.Warden.WardenCheckRow>().AnyAsync(ct).ConfigureAwait(false))
                {
                    db.Set<World.Warden.WardenCheckRow>().AddRange(World.Warden.WardenCheckSeed.Rows.Select(r => new World.Warden.WardenCheckRow
                    {
                        Id = r.Id, Type = r.Type, Str = r.Str, Data = r.Data, Address = r.Address, Length = r.Length, Result = r.Result,
                        Flags = r.Flags, Penalty = r.Penalty, BuildMin = r.BuildMin, BuildMax = r.BuildMax, Comment = r.Comment,
                    }));
                    added = true;
                }

                // The shipped bot goal ladder (World.Rules.PlayerbotLadderSeed), each table into an empty one only; the brain also
                // falls back to the same rows from code and warns when the tables hold none.
                if (!await db.Set<World.Rules.PlayerbotGoalRow>().AnyAsync(ct).ConfigureAwait(false))
                {
                    db.Set<World.Rules.PlayerbotGoalRow>().AddRange(World.Rules.PlayerbotLadderSeed.Goals());
                    added = true;
                }

                if (!await db.Set<World.Rules.RuleRow>().AnyAsync(ct).ConfigureAwait(false))
                {
                    db.Set<World.Rules.RuleRow>().AddRange(World.Rules.PlayerbotLadderSeed.Rules());
                    db.Set<World.Rules.RuleActionRow>().AddRange(World.Rules.PlayerbotLadderSeed.Actions());
                    added = true;
                }

                // How bots tell each festival (World.WorldState.HolidayFlavourSeed), into an empty table only: nothing else installs the shipped rows.
                if (!await db.Set<World.WorldState.HolidayFlavourRow>().AnyAsync(ct).ConfigureAwait(false))
                {
                    db.Set<World.WorldState.HolidayFlavourRow>().AddRange(World.WorldState.HolidayFlavourSeed.Rows.Select(r => new World.WorldState.HolidayFlavourRow
                    {
                        HolidayId = r.HolidayId, FlavourKey = r.FlavourKey, Name = r.Name, Emote1 = r.Emote1, Emote2 = r.Emote2, Emote3 = r.Emote3,
                        Gather = (byte)(r.Gather ? 1 : 0),
                    }));
                    added = true;
                }

                if (added)
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                // The shipped tuning rows (Kernel TuningDefaults; also sql/content/world/*/0003_tuning_tables.sql), into each empty table only.
                await new World.Tuning.EfTuningContentStore(db).SeedEmptyTablesAsync(ArcaneCore.Kernel.Tuning.TuningDefaults.Content, ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }
}
