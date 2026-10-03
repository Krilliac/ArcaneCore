using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Kernel.WorldData.Threat;

/// <summary>
/// One <c>spell_threat</c> row (vmangos <c>SpellMgr::LoadSpellThreats</c>, Spells/SpellMgr.cpp:834-875): the flat threat
/// a spell adds once per hit target, the multiplier of the threat its damage and healing cause, and the effects (bit i = effect i)
/// that do not count as causing the flat threat.
/// </summary>
public sealed record SpellThreatRecord(uint Entry, ushort Threat, float Multiplier, byte InverseEffectMask);

/// <summary>The <c>spell_threat</c> table of the world database, immutable once loaded.</summary>
public sealed class SpellThreatContent
{
    private readonly Dictionary<uint, SpellThreatRecord> _byEntry = [];

    public static SpellThreatContent Empty { get; } = new([]);

    /// <summary>A duplicate entry is a hard error (fail closed): the table's key is the spell.</summary>
    public SpellThreatContent(IEnumerable<SpellThreatRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (SpellThreatRecord row in rows)
        {
            if (!_byEntry.TryAdd(row.Entry, row))
            {
                throw new InvalidDataException($"spell_threat lists spell {row.Entry} twice");
            }
        }
    }

    public int Count => _byEntry.Count;

    public IEnumerable<SpellThreatRecord> Rows => _byEntry.Values;

    public SpellThreatRecord? Find(uint entry) => _byEntry.GetValueOrDefault(entry);

    /// <summary>
    /// The table the game reads (vmangos <c>SpellRankHelper</c> with <c>DoSpellThreat</c>, SpellMgr.cpp:127-182 and :770-875): a listed spell
    /// that does not exist is dropped; a listed higher rank of a chain needs its own threat (a custom rank with no flat threat is dropped);
    /// every higher rank of a listed first rank that has no row of its own inherits the first rank's data. A custom rank whose data
    /// equals its first rank's is kept and reported as redundant; a custom rank whose first rank has no row is reported. Reports go to
    /// <paramref name="report"/> (vmangos logs them as database errors).
    /// </summary>
    public IReadOnlyDictionary<uint, SpellThreatRecord> Resolve(SpellRankChains ranks, Func<uint, bool>? spellExists = null, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        spellExists ??= static _ => true;
        report ??= static _ => { };
        var map = new Dictionary<uint, SpellThreatRecord>();
        var firstRanks = new SortedSet<uint>();
        var firstRanksWithCustomRanks = new SortedSet<uint>();
        foreach (SpellThreatRecord row in _byEntry.Values.OrderBy(r => r.Entry))
        {
            if (!spellExists(row.Entry))
            {
                report($"Spell {row.Entry} listed in `spell_threat` does not exist");
                continue;
            }

            uint first = ranks.First(row.Entry);
            if (first != 0)
            {
                firstRanks.Add(first);
                if (first != row.Entry)
                {
                    if (row.Threat == 0)
                    {
                        report($"Spell {row.Entry} listed in `spell_threat` is not first rank ({first}) in chain and has no threat");
                        continue;
                    }

                    firstRanksWithCustomRanks.Add(first);
                }
            }

            map[row.Entry] = row;
        }

        foreach (uint first in firstRanksWithCustomRanks)
        {
            if (!map.ContainsKey(first))
            {
                report($"Spell {first} must be listed in `spell_threat` as first rank for listed custom ranks of spell but not found!");
            }
        }

        foreach (uint first in firstRanks)
        {
            if (!map.TryGetValue(first, out SpellThreatRecord? data))
            {
                continue;
            }

            for (uint next = ranks.Next(first); next != 0; next = ranks.Next(next))
            {
                if (!map.TryGetValue(next, out SpellThreatRecord? own))
                {
                    map[next] = data with { Entry = next };
                }
                else if (own.Threat == data.Threat && own.Multiplier == data.Multiplier && own.InverseEffectMask == data.InverseEffectMask)
                {
                    report($"Spell {next} listed in `spell_threat` as custom rank has same data as Rank 1, so redundant");
                }
            }
        }

        return map;
    }
}

/// <summary>Reads <c>spell_threat</c> (the world data store of the threat table).</summary>
public interface ISpellThreatDataStore
{
    Task<SpellThreatContent> LoadAsync(CancellationToken cancellationToken = default);
}
