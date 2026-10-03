using ArcaneCore.Game.Entities;

namespace ArcaneCore.Game.WorldState.Exploration;

/// <summary>The explored-zones words as stored in a player's update fields (<c>PLAYER_EXPLORED_ZONES_1..64</c>).</summary>
public static class ExploredZonesFields
{
    /// <summary>The 64 words of <paramref name="player"/>.</summary>
    public static uint[] Read(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint[] words = new uint[ExploredZones.WordCount];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = player.GetUInt32(UpdateFields.PlayerExploredZones1 + i);
        }

        return words;
    }

    /// <summary>Set the 64 words (vmangos <c>_LoadIntoDataField(PLAYER_EXPLORED_ZONES_1)</c> at login).</summary>
    public static void Write(Player player, ReadOnlySpan<uint> words)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (words.Length != ExploredZones.WordCount)
        {
            throw new ArgumentException($"expected {ExploredZones.WordCount} words", nameof(words));
        }

        for (int i = 0; i < words.Length; i++)
        {
            player.SetUInt32(UpdateFields.PlayerExploredZones1 + i, words[i]);
        }
    }

    /// <summary>
    /// Record a flag in the player's fields. Returns the outcome; the fields change only for
    /// <see cref="ExploreOutcome.Discovered"/>.
    /// </summary>
    public static ExploreOutcome Mark(Player player, uint areaFlag)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (areaFlag == ExploredZones.NoAreaFlag)
        {
            return ExploreOutcome.NoArea;
        }

        if (!ExploredZones.TryLocate(areaFlag, out int offset, out uint mask))
        {
            return ExploreOutcome.OutOfRange;
        }

        uint current = player.GetUInt32(UpdateFields.PlayerExploredZones1 + offset);
        if ((current & mask) != 0)
        {
            return ExploreOutcome.AlreadyExplored;
        }

        player.SetUInt32(UpdateFields.PlayerExploredZones1 + offset, current | mask);
        return ExploreOutcome.Discovered;
    }
}
