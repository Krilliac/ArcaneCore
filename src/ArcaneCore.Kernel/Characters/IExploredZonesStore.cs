using System.Globalization;
using System.Text;

namespace ArcaneCore.Kernel.Characters;

/// <summary>
/// Persistence of a character's explored-zones words (<c>PLAYER_EXPLORED_ZONES_1..64</c>;
/// vmangos <c>characters.explored_zones</c> longtext, sql/characters.sql:92, loaded at
/// Player.cpp:14648 and saved at :16481). Characters schema, docs/areas/world-state.md.
/// </summary>
public interface IExploredZonesStore
{
    /// <summary>The 64 stored words, or null when the character has no row (nothing explored). A malformed row throws <see cref="InvalidDataException"/> (fail closed).</summary>
    Task<uint[]?> LoadAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace the words; a missing character is ignored (deleted while queued).</summary>
    Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default);

    /// <summary>Remove the row of a character id about to be reused at creation.</summary>
    Task DeleteAsync(int characterId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The vmangos text form of the words (<c>_LoadIntoDataField</c> / the save loop): decimal u32
/// values each followed by one space, 64 of them. Parsing is strict: any other count or a non-numeric
/// token is an error, never a silent zero.
/// </summary>
public static class ExploredZonesText
{
    public const int WordCount = 64;

    public static string Format(IReadOnlyList<uint> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count != WordCount)
        {
            throw new ArgumentException($"expected {WordCount} words", nameof(words));
        }

        var text = new StringBuilder(WordCount * 11);
        foreach (uint word in words)
        {
            text.Append(word.ToString(CultureInfo.InvariantCulture)).Append(' ');
        }

        return text.ToString();
    }

    public static uint[] Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != WordCount)
        {
            throw new InvalidDataException($"explored_zones has {tokens.Length} values, expected {WordCount}");
        }

        uint[] words = new uint[WordCount];
        for (int i = 0; i < WordCount; i++)
        {
            if (!uint.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out words[i]))
            {
                throw new InvalidDataException($"explored_zones value {i} is not an unsigned integer");
            }
        }

        return words;
    }
}
