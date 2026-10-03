using System.Collections.Frozen;
using ArcaneCore.Protocol;

namespace ArcaneCore.Game.WorldState.Exploration;

/// <summary>The result of recording an area flag in the explored-zones words.</summary>
public enum ExploreOutcome
{
    /// <summary>Flag 0xFFFF (no area data on the terrain cell): nothing to record.</summary>
    NoArea,

    /// <summary>The flag points beyond the 64 words (vmangos logs "Wrong area flag" and returns).</summary>
    OutOfRange,

    /// <summary>The bit was already set.</summary>
    AlreadyExplored,

    /// <summary>The bit was newly set.</summary>
    Discovered,
}

/// <summary>
/// The explored-zones bit array: <c>PLAYER_EXPLORED_ZONES_1..64</c>, 64 words of 32 flags
/// (vmangos <c>PLAYER_EXPLORED_ZONES_SIZE</c>, Player.h:70; bit math of
/// <c>CheckAreaExploreAndOutdoor</c>, Player.cpp:6089-6204).
/// </summary>
public static class ExploredZones
{
    /// <summary>vmangos <c>PLAYER_EXPLORED_ZONES_SIZE</c>.</summary>
    public const int WordCount = 64;

    /// <summary>The terrain "no area" flag value.</summary>
    public const uint NoAreaFlag = 0xFFFF;

    /// <summary>Whether the bit of <paramref name="areaFlag"/> is set.</summary>
    public static bool IsExplored(ReadOnlySpan<uint> words, uint areaFlag)
        => TryLocate(areaFlag, out int offset, out uint mask) && offset < words.Length && (words[offset] & mask) != 0;

    /// <summary>
    /// Record the flag. Uses an unsigned shift (vmangos writes <c>(uint32)(1 &lt;&lt; (flag % 32))</c>,
    /// the same bit pattern for bit 31).
    /// </summary>
    public static ExploreOutcome Mark(Span<uint> words, uint areaFlag)
    {
        if (areaFlag == NoAreaFlag)
        {
            return ExploreOutcome.NoArea;
        }

        if (!TryLocate(areaFlag, out int offset, out uint mask) || offset >= words.Length)
        {
            return ExploreOutcome.OutOfRange;
        }

        if ((words[offset] & mask) != 0)
        {
            return ExploreOutcome.AlreadyExplored;
        }

        words[offset] |= mask;
        return ExploreOutcome.Discovered;
    }

    /// <summary>The word offset and bit mask of a flag; false when the offset is not below <see cref="WordCount"/>.</summary>
    public static bool TryLocate(uint areaFlag, out int offset, out uint mask)
    {
        offset = (int)(areaFlag / 32);
        mask = 1u << (int)(areaFlag % 32);
        return offset < WordCount;
    }
}

/// <summary>
/// The <c>exploration_basexp</c> table (level to base XP): data loaded from the world database,
/// never compiled in. A level without a row yields 0 (vmangos <c>ObjectMgr::GetBaseXP</c>, :8516).
/// </summary>
public sealed class ExplorationBaseXpTable
{
    private readonly FrozenDictionary<uint, uint> _byLevel;

    public ExplorationBaseXpTable(IEnumerable<KeyValuePair<uint, uint>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _byLevel = rows.ToFrozenDictionary();
    }

    /// <summary>No rows: every exploration gives 0 XP (a documented limit of a world without imported data).</summary>
    public static ExplorationBaseXpTable Empty { get; } = new([]);

    public int Count => _byLevel.Count;

    public uint Get(uint level) => _byLevel.TryGetValue(level, out uint xp) ? xp : 0;
}

/// <summary>The exploration experience formula (vmangos Player.cpp:6171-6196).</summary>
public static class ExplorationXp
{
    /// <summary>vmangos <c>CONFIG_UINT32_MAX_PLAYER_LEVEL</c> default.</summary>
    public const uint DefaultMaxPlayerLevel = 60;

    /// <summary>
    /// XP for discovering an area of <paramref name="areaLevel"/> at <paramref name="playerLevel"/>.
    /// 0 when the area has no level or the player is at the maximum level. Float32 like the C++:
    /// the integer division by 100 happens before the rate multiply, which converts to float.
    /// </summary>
    public static uint Compute(uint playerLevel, int areaLevel, ExplorationBaseXpTable table, float rate, uint maxPlayerLevel = DefaultMaxPlayerLevel)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (areaLevel <= 0 || playerLevel >= maxPlayerLevel)
        {
            return 0;
        }

        int diff = (int)playerLevel - areaLevel;
        if (diff < -5)
        {
            return (uint)(table.Get(playerLevel + 5) * rate);
        }

        if (diff > 5)
        {
            int percent = 100 - ((diff - 5) * 5);
            percent = Math.Clamp(percent, 0, 100);
            uint baseXp = table.Get((uint)areaLevel);
            return (uint)((float)(baseXp * (uint)percent / 100) * rate);
        }

        return (uint)(table.Get((uint)areaLevel) * rate);
    }
}

/// <summary>Exploration packet bodies.</summary>
public static class ExplorationPackets
{
    /// <summary>
    /// SMSG_EXPLORATION_EXPERIENCE: u32 area id, u32 experience (vmangos Server/Packets/Misc.cpp:833-837,
    /// wow_messages smsg_exploration_experience.wowm). vmangos sends it for every discovered area
    /// that has an entry, even with 0 XP.
    /// </summary>
    public static byte[] Build(uint areaId, uint experience)
    {
        var writer = new PacketWriter(8);
        writer.WriteUInt32(areaId);
        writer.WriteUInt32(experience);
        return writer.ToArray();
    }
}
