using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Kernel.WorldData.Procs;

/// <summary>
/// One <c>spell_proc_event</c> row (vmangos <c>SpellProcEventEntry</c>, Spells/SpellMgr.h:43-53, loaded by <c>SpellMgr::LoadSpellProcEvents</c>,
/// Spells/SpellMgr.cpp:316-365): the conditions a proc aura of <see cref="Entry"/> needs beyond its Spell.dbc procFlags and procChance.
/// </summary>
/// <param name="Entry">The aura spell.</param>
/// <param name="SchoolMask">When non-zero, the school mask the triggering spell (or a melee hit, physical) must share.</param>
/// <param name="SpellFamilyName">When non-zero, the family the triggering spell must belong to.</param>
/// <param name="SpellFamilyMask0">Effect 0's class mask: when non-zero the triggering spell's SpellFamilyFlags must share a bit.</param>
/// <param name="SpellFamilyMask1">Effect 1's class mask.</param>
/// <param name="SpellFamilyMask2">Effect 2's class mask.</param>
/// <param name="ProcFlags">When non-zero, replaces the spell's own procFlags.</param>
/// <param name="ProcEx">The extra requirements (hit, crit, dodge, ... <c>ProcFlagsEx</c>).</param>
/// <param name="PpmRate">Procs per minute for the attacker side (the chance follows the weapon speed); 0 uses the chance.</param>
/// <param name="CustomChance">When non-zero, replaces the spell's procChance.</param>
/// <param name="Cooldown">The hidden cooldown in milliseconds put on the triggered spell after a proc.</param>
public sealed record SpellProcEventRecord(
    uint Entry,
    uint SchoolMask,
    uint SpellFamilyName,
    ulong SpellFamilyMask0,
    ulong SpellFamilyMask1,
    ulong SpellFamilyMask2,
    uint ProcFlags,
    uint ProcEx,
    float PpmRate,
    float CustomChance,
    uint Cooldown)
{
    /// <summary>The class mask of effect <paramref name="effectIndex"/> (vmangos <c>spellFamilyMask[i]</c>).</summary>
    public ulong FamilyMask(int effectIndex) => effectIndex switch
    {
        0 => SpellFamilyMask0,
        1 => SpellFamilyMask1,
        2 => SpellFamilyMask2,
        _ => 0,
    };

    /// <summary>The same condition data for another spell (the rank fill copies the first rank's row).</summary>
    public SpellProcEventRecord ForSpell(uint entry) => this with { Entry = entry };

    /// <summary>Whether the row carries nothing a proc could use (vmangos "not have any useful data", SpellMgr.cpp:279-292).</summary>
    public bool IsEmpty => SchoolMask == 0 && ProcFlags == 0 && ProcEx == 0 && PpmRate == 0f && CustomChance == 0f && Cooldown == 0
        && SpellFamilyName == 0 && SpellFamilyMask0 == 0 && SpellFamilyMask1 == 0 && SpellFamilyMask2 == 0;
}

/// <summary>The <c>spell_proc_event</c> table of the world database, immutable once loaded.</summary>
public sealed class SpellProcEventContent
{
    private readonly Dictionary<uint, SpellProcEventRecord> _byEntry = [];

    public static SpellProcEventContent Empty { get; } = new([]);

    /// <summary>A duplicate entry is a hard error (fail closed): the table's key is the spell.</summary>
    public SpellProcEventContent(IEnumerable<SpellProcEventRecord> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        foreach (SpellProcEventRecord row in rows)
        {
            if (!_byEntry.TryAdd(row.Entry, row))
            {
                throw new InvalidDataException($"spell_proc_event lists spell {row.Entry} twice");
            }
        }
    }

    public int Count => _byEntry.Count;

    public IEnumerable<SpellProcEventRecord> Rows => _byEntry.Values;

    public SpellProcEventRecord? Find(uint entry) => _byEntry.GetValueOrDefault(entry);

    /// <summary>
    /// The table the game reads (vmangos <c>SpellRankHelper</c> with <c>DoSpellProcEvent</c>, Spells/SpellMgr.cpp:127-315): a listed spell that does
    /// not exist is dropped; a listed higher rank of a chain must have a PPM rate of its own, otherwise it is dropped ("is not first rank");
    /// every higher rank of a listed first rank that has no row of its own inherits the first rank's row. A custom rank that differs from its
    /// first rank in anything but the PPM rate is kept and reported; a custom rank whose first rank has no row is reported. Reports go to
    /// <paramref name="report"/> (vmangos logs them as database errors).
    /// </summary>
    public IReadOnlyDictionary<uint, SpellProcEventRecord> Resolve(SpellRankChains ranks, Func<uint, bool>? spellExists = null, Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(ranks);
        spellExists ??= static _ => true;
        report ??= static _ => { };
        var map = new Dictionary<uint, SpellProcEventRecord>();
        var firstRanks = new SortedSet<uint>();
        var firstRanksWithCustomRanks = new SortedSet<uint>();
        foreach (SpellProcEventRecord row in _byEntry.Values.OrderBy(r => r.Entry))
        {
            if (!spellExists(row.Entry))
            {
                report($"Spell {row.Entry} listed in `spell_proc_event` does not exist");
                continue;
            }

            uint first = ranks.First(row.Entry);
            if (first != 0)
            {
                firstRanks.Add(first);
                if (first != row.Entry)
                {
                    // DoSpellProcEvent::IsValidCustomRank: only rank-dependent PPM rows may stand for a higher rank.
                    if (row.PpmRate == 0f)
                    {
                        report($"Spell {row.Entry} listed in `spell_proc_event` is not first rank ({first}) in chain");
                        continue;
                    }

                    firstRanksWithCustomRanks.Add(first);
                }
            }

            if (row.IsEmpty)
            {
                report($"Spell {row.Entry} listed in `spell_proc_event` not have any useful data");
            }

            map[row.Entry] = row;
        }

        foreach (uint first in firstRanksWithCustomRanks)
        {
            if (!map.ContainsKey(first))
            {
                report($"Spell {first} must be listed in `spell_proc_event` as first rank for listed custom ranks of spell but not found!");
            }
        }

        foreach (uint first in firstRanks)
        {
            if (!map.TryGetValue(first, out SpellProcEventRecord? data))
            {
                continue;
            }

            for (uint next = ranks.Next(first); next != 0; next = ranks.Next(next))
            {
                if (!map.TryGetValue(next, out SpellProcEventRecord? own))
                {
                    map[next] = data.ForSpell(next);
                }
                else if (own.SchoolMask != data.SchoolMask || own.SpellFamilyName != data.SpellFamilyName
                    || own.SpellFamilyMask0 != data.SpellFamilyMask0 || own.SpellFamilyMask1 != data.SpellFamilyMask1 || own.SpellFamilyMask2 != data.SpellFamilyMask2
                    || own.ProcFlags != data.ProcFlags || own.ProcEx != data.ProcEx || own.CustomChance != data.CustomChance || own.Cooldown != data.Cooldown)
                {
                    // "only ppm allowed has been different from first rank" (SpellMgr.cpp:195-226): the custom row is kept as listed.
                    report($"Spell {next} listed in `spell_proc_event` as custom rank differs from first rank {first} in more than the PPM rate");
                }
            }
        }

        return map;
    }
}

/// <summary>Reads <c>spell_proc_event</c> (the world data store of the proc conditions).</summary>
public interface ISpellProcEventDataStore
{
    Task<SpellProcEventContent> LoadAsync(CancellationToken cancellationToken = default);
}
