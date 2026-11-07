namespace ArcaneCore.Kernel.WorldData.Pets;

/// <summary>
/// One <c>pet_levelstats</c> row: the stats a summoned pet of creature <see cref="Entry"/> has at
/// <see cref="Level"/> (vmangos <c>PetLevelInfo</c>, ObjectMgr.cpp:4406-4470; column names are
/// vmangos': <c>entry, level, health, mana, armor, dmg_min, dmg_max, strength, agility, stamina,
/// intellect, spirit</c>).
/// </summary>
public sealed record PetLevelStats(
    uint Entry, byte Level, uint Health, uint Mana, uint Armor, float MinDamage, float MaxDamage,
    uint Strength, uint Agility, uint Stamina, uint Intellect, uint Spirit);

/// <summary>One <c>petcreateinfo_spell</c> row: the spells a freshly summoned pet of creature <see cref="Entry"/> starts with.</summary>
public sealed record PetCreateSpells(uint Entry, IReadOnlyList<uint> Spells);

/// <summary>
/// One <c>pet_name_generation</c> row: a <see cref="Word"/> that is the first (<see cref="SecondHalf"/> false) or second half of a generated
/// name for pets of creature <see cref="Entry"/> (vmangos <c>ObjectMgr::LoadPetNames</c>, ObjectMgr.cpp:8530-8570: the <c>half</c> column picks
/// the list).
/// </summary>
public sealed record PetNamePart(uint Entry, bool SecondHalf, string Word);

/// <summary>
/// The pet tables of the world database, immutable once loaded. Level data follows vmangos
/// <c>ObjectMgr::LoadPetLevelInfo</c> / <c>GetPetLevelInfo</c> (ObjectMgr.cpp:4406-4516): levels
/// outside 1..<see cref="MaxLevel"/> are ignored, a creature without level 1 data is a hard error
/// (vmangos exits), a level without data repeats the level below it, and a lookup above the maximum
/// level answers the maximum level.
/// </summary>
public sealed class PetContent
{
    /// <summary>vmangos <c>CONFIG_UINT32_MAX_PLAYER_LEVEL</c> default (mangosd.conf).</summary>
    public const int MaxLevel = 60;

    private readonly Dictionary<uint, PetLevelStats[]> _levelStats = [];
    private readonly Dictionary<uint, IReadOnlyList<uint>> _createSpells = [];
    private readonly Dictionary<uint, string[]> _firstHalves = [];
    private readonly Dictionary<uint, string[]> _secondHalves = [];

    /// <param name="levelStats">The <c>pet_levelstats</c> rows.</param>
    /// <param name="createSpells">The <c>petcreateinfo_spell</c> rows.</param>
    /// <param name="nameParts">The <c>pet_name_generation</c> rows (none: no pet gets a generated name).</param>
    public PetContent(IEnumerable<PetLevelStats> levelStats, IEnumerable<PetCreateSpells> createSpells, IEnumerable<PetNamePart>? nameParts = null)
    {
        ArgumentNullException.ThrowIfNull(levelStats);
        ArgumentNullException.ThrowIfNull(createSpells);
        if (nameParts is not null)
        {
            foreach (IGrouping<(uint Entry, bool SecondHalf), PetNamePart> group in nameParts
                         .Where(part => !string.IsNullOrEmpty(part.Word))
                         .GroupBy(part => (part.Entry, part.SecondHalf)))
            {
                (group.Key.SecondHalf ? _secondHalves : _firstHalves)[group.Key.Entry] = [.. group.Select(part => part.Word)];
            }
        }

        foreach (PetLevelStats row in levelStats)
        {
            if (row.Level is < 1 or > MaxLevel)
            {
                continue; // "Wrong (<1) level" / "Unused (> MaxPlayerLevel) level": ignored
            }

            if (!_levelStats.TryGetValue(row.Entry, out PetLevelStats[]? levels))
            {
                _levelStats[row.Entry] = levels = new PetLevelStats[MaxLevel];
            }

            levels[row.Level - 1] = row;
        }

        // Fill gaps and check integrity (ObjectMgr.cpp:4492-4516)
        foreach ((uint entry, PetLevelStats[] levels) in _levelStats)
        {
            if (levels[0] is not { Health: > 0 })
            {
                throw new InvalidDataException($"Creature {entry} does not have pet stats data for Level 1!");
            }

            for (int level = 1; level < MaxLevel; level++)
            {
                if (levels[level] is not { Health: > 0 })
                {
                    levels[level] = levels[level - 1] with { Level = (byte)(level + 1) };
                }
            }
        }

        foreach (PetCreateSpells row in createSpells)
        {
            // vmangos stops at the first 0 spell id and skips an entry that has no spell at all (ObjectMgr.cpp:6475-6545)
            uint[] spells = [.. row.Spells.TakeWhile(spell => spell != 0)];
            if (spells.Length > 0)
            {
                _createSpells[row.Entry] = spells;
            }
        }
    }

    public static PetContent Empty { get; } = new([], []);

    /// <summary>Creatures that have level stats.</summary>
    public int LevelStatsEntryCount => _levelStats.Count;

    /// <summary>Creatures that have create spells.</summary>
    public int CreateSpellEntryCount => _createSpells.Count;

    /// <summary>Creatures that have generated-name parts in both halves (the only ones <see cref="GeneratePetName"/> answers for).</summary>
    public int NameEntryCount => _firstHalves.Keys.Count(entry => _secondHalves.ContainsKey(entry));

    /// <summary>
    /// vmangos ObjectMgr::GeneratePetName (ObjectMgr.cpp:8607-8624): a random word of the first list followed by a random word of the second list
    /// of <paramref name="entry"/>. Null when either list is empty; vmangos then falls back to the creature family's pet name or the creature name,
    /// and the caller keeps the creature name (no CreatureFamily pet-name table is loaded). <paramref name="random"/> is used on the calling thread only.
    /// </summary>
    public string? GeneratePetName(uint entry, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        if (!_firstHalves.TryGetValue(entry, out string[]? first) || !_secondHalves.TryGetValue(entry, out string[]? second))
        {
            return null;
        }

        return first[random.Next(first.Length)] + second[random.Next(second.Length)];
    }

    /// <summary>Every creature entry that has level stats or create spells (a summoned pet or guardian template), in no particular order, possibly with repeats.</summary>
    public IEnumerable<uint> Entries => _levelStats.Keys.Concat(_createSpells.Keys);

    /// <summary>vmangos ObjectMgr::GetPetLevelInfo: the stats of <paramref name="entry"/> at <paramref name="level"/> (capped at <see cref="MaxLevel"/>), or null.</summary>
    public PetLevelStats? FindLevelStats(uint entry, int level)
        => _levelStats.TryGetValue(entry, out PetLevelStats[]? levels) ? levels[Math.Clamp(level, 1, MaxLevel) - 1] : null;

    /// <summary>vmangos ObjectMgr::GetPetCreateSpellEntry: the spells a new pet of <paramref name="entry"/> knows (empty when none).</summary>
    public IReadOnlyList<uint> GetCreateSpells(uint entry) => _createSpells.GetValueOrDefault(entry) ?? [];
}

/// <summary>Loads the pet tables from the world database.</summary>
public interface IPetDataStore
{
    Task<PetContent> LoadAsync(CancellationToken cancellationToken = default);
}
