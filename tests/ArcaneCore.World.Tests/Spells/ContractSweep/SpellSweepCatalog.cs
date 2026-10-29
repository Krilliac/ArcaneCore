using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.World.Spells;
using ArcaneCore.World.Tests.ClientData;

namespace ArcaneCore.World.Tests.Spells.ContractSweep;

/// <summary>
/// The client's spells as the sweep sees them: Spell.dbc (and the four index DBCs) through the production importer and store
/// factory, plus SkillLineAbility.dbc for which spells a player class can learn. Loaded once per process.
/// </summary>
internal sealed class SpellSweepCatalog
{
    /// <summary>Set to 1 to sweep every Spell.dbc row instead of the default selection.</summary>
    public const string FullVariable = "ARCANECORE_SPELL_SWEEP_FULL";

    /// <summary>The seed of the default run's sample of the non-player spells (fixed, so a run is reproducible).</summary>
    public const int SampleSeed = 5875;

    /// <summary>How many non-player spells the default run samples.</summary>
    public const int DefaultSampleSize = 1500;

    private static readonly Lazy<SpellSweepCatalog> s_shared = new(() => Load(RealClientDbcFactAttribute.DbcDirectory));

    private SpellSweepCatalog(SpellStore store, IReadOnlyList<SpellInfo> spells, IReadOnlyDictionary<uint, uint> classMasks)
    {
        Store = store;
        Spells = spells;
        ClassMasks = classMasks;
    }

    public static SpellSweepCatalog Shared => s_shared.Value;

    public SpellStore Store { get; }

    /// <summary>Every Spell.dbc row, by id.</summary>
    public IReadOnlyList<SpellInfo> Spells { get; }

    /// <summary>The OR of the SkillLineAbility.dbc class masks of each spell that has a row with a non-zero class mask.</summary>
    public IReadOnlyDictionary<uint, uint> ClassMasks { get; }

    public static bool FullRequested => Environment.GetEnvironmentVariable(FullVariable) == "1";

    public static SpellSweepCatalog Load(string directory)
    {
        SpellDbcContent dbc = SpellDbcImporter.ReadDirectory(directory);
        var content = new SpellContent(dbc.Spells, dbc.CastTimes, dbc.Durations, dbc.Ranges, dbc.Radii, [], []);
        SpellStore store = SpellStoreFactory.Build(content);
        IReadOnlyList<SkillLineAbilityRecord> abilities =
            NpcServiceDbcReaders.ReadSkillLineAbilityRecords(ArcaneCore.Data.ClientData.DbcFile.Load(Path.Combine(directory, "SkillLineAbility.dbc")));
        var masks = new Dictionary<uint, uint>();
        foreach (SkillLineAbilityRecord row in abilities.Where(r => r.ClassMask != 0))
        {
            masks[row.SpellId] = masks.GetValueOrDefault(row.SpellId) | row.ClassMask;
        }

        SpellInfo[] spells = [.. dbc.Spells.Select(r => store.Get(r.Id)).OfType<SpellInfo>().OrderBy(s => s.Id)];
        return new SpellSweepCatalog(store, spells, masks);
    }

    /// <summary>The class family of a spell (Spell.dbc SpellFamilyName: 3 mage .. 11 shaman, the constants the spell scripts use).</summary>
    public static Class? ClassOfFamily(uint family) => family switch
    {
        3 => Class.Mage,
        4 => Class.Warrior,
        5 => Class.Warlock,
        6 => Class.Priest,
        7 => Class.Druid,
        8 => Class.Rogue,
        9 => Class.Hunter,
        10 => Class.Paladin,
        11 => Class.Shaman,
        _ => null,
    };

    /// <summary>The player class a spell belongs to: its family, else the lowest class of its SkillLineAbility class mask, else none.</summary>
    public Class? ClassOf(SpellInfo spell)
    {
        if (ClassOfFamily(spell.SpellFamilyName) is { } byFamily)
        {
            return byFamily;
        }

        if (ClassMasks.TryGetValue(spell.Id, out uint mask))
        {
            for (int bit = 0; bit < 11; bit++)
            {
                if ((mask & (1u << bit)) != 0 && Enum.IsDefined((Class)(bit + 1)))
                {
                    return (Class)(bit + 1);
                }
            }
        }

        return null;
    }

    /// <summary>Whether a player can have the spell: a class family, or a SkillLineAbility row with a class mask.</summary>
    public bool IsPlayerCastable(SpellInfo spell) => ClassOfFamily(spell.SpellFamilyName) is not null || ClassMasks.ContainsKey(spell.Id);

    /// <summary>
    /// The spells this run sweeps: every row when <see cref="FullRequested"/>; otherwise every player-castable spell plus a seeded
    /// sample of <see cref="DefaultSampleSize"/> of the rest. Ordered by id.
    /// </summary>
    public IReadOnlyList<SpellInfo> Select(bool full, int sampleSize = DefaultSampleSize)
    {
        if (full)
        {
            return Spells;
        }

        List<SpellInfo> chosen = [.. Spells.Where(IsPlayerCastable)];
        SpellInfo[] rest = [.. Spells.Where(s => !IsPlayerCastable(s))];
        var random = new Random(SampleSeed);
        chosen.AddRange(rest.OrderBy(_ => random.Next()).Take(sampleSize));
        return [.. chosen.OrderBy(s => s.Id)];
    }
}
