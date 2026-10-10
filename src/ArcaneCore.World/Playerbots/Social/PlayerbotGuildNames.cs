using ArcaneCore.Data.Content.Names;
using ArcaneCore.Game.Guilds;
using ArcaneCore.World.Chat;
using ArcaneCore.World.Playerbots.Chat;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>Why a guild name was refused (<see cref="PlayerbotGuildNames.Screen"/>).</summary>
internal enum PlayerbotGuildNameVerdict
{
    /// <summary>Every check passed.</summary>
    Ok,

    /// <summary>The charter name rules refused it (vmangos IsValidCharterName: length, one script; or a reserved charter name).</summary>
    Invalid,

    /// <summary>A guild already has the name (vmangos GuildMgr::GetGuildByName, case-insensitive).</summary>
    Taken,

    /// <summary>A pending charter holds the name.</summary>
    PetitionHeld,

    /// <summary>The charter antispam filter calls it spam (vmangos AntispamInterface).</summary>
    Spam,

    /// <summary>The name catalog (NamesProfanity / NamesReserved and the chat word filter's name patterns) refuses the name or one of its words.</summary>
    NameCatalog,

    /// <summary>The chat word filter censors or blocks the name.</summary>
    WordFilter,

    /// <summary>The bot chat safety filter flags the name (or could not judge it in time).</summary>
    Unsafe,
}

/// <summary>
/// What screens a generated guild name: the charter rules, the live guild and petition names, and each filter the server has installed.
/// A filter that is not installed (null) adds no check; the others are never skipped. Built from the live services for each founding.
/// </summary>
internal sealed record PlayerbotGuildNameRules(
    GuildOptions Options,
    ICharterNameBlacklist? Blacklist,
    ICharterAntispamFilter? Antispam,
    Func<string, bool> IsTaken,
    Func<string, bool> HoldsPetition,
    NameCatalog? Catalog,
    ChatWordFilter? WordFilter,
    PlayerbotChatSafetyFilter? Safety);

/// <summary>
/// Plausible guild names for bots (docs/areas/playbots-social.md): a seeded draw from neutral word lists (no canon character or guild
/// names), letters and single spaces only, at most 24 characters (<see cref="GuildOptions.MaxCharterNameLength"/>). Every candidate is
/// screened, in this order, before a bot may use it: the charter rules (<see cref="CharterNameRules.IsValid"/>, with the reserved list), a
/// taken guild name, a pending charter's name, the antispam filter, the name catalog (the whole name and each word), the chat word filter
/// (the name must come back unchanged) and the bot chat safety filter (every category). When no candidate passes, there is no name and no guild
/// is founded (fail closed).
/// </summary>
internal static class PlayerbotGuildNames
{
    /// <summary>The most candidates drawn for one founding.</summary>
    internal const int MaxAttempts = 32;

    private static readonly string[] Adjectives =
    [
        "Silver", "Crimson", "Golden", "Silent", "Iron", "Hollow", "Amber", "Wandering", "Stormy", "Ashen", "Verdant", "Radiant", "Frosted",
        "Gilded", "Ember", "Twilight", "Brave", "Steadfast", "Wild", "Swift", "Gentle", "Northern", "Western", "Lucky", "Merry",
    ];

    private static readonly string[] Nouns =
    [
        "Wolves", "Ravens", "Lions", "Blades", "Banner", "Hearth", "Lanterns", "Shield", "Oaks", "Wardens", "Pilgrims", "Harvest", "Compass",
        "Anvil", "Falcons", "Thistle", "Cairn", "Company", "Vanguard", "Travelers", "Foxes", "Bears", "Embers", "Stags",
    ];

    private static readonly string[] Places =
    [
        "Vale", "Marsh", "Highlands", "Shore", "Crossing", "Ridge", "Glen", "Reach", "Dunes", "Meadow", "Harbor", "Pass", "Woods",
    ];

    /// <summary>One candidate name from <paramref name="random"/>: letters and single spaces, 4 to 24 characters. Not screened.</summary>
    internal static string Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            string name = random.Next(4) switch
            {
                0 => $"{Pick(Adjectives, random)} {Pick(Nouns, random)}",
                1 => $"{Pick(Nouns, random)} of the {Pick(Places, random)}",
                2 => $"Order of the {Pick(Nouns, random)}",
                _ => $"The {Pick(Adjectives, random)} {Pick(Nouns, random)}",
            };
            if (name.Length <= GuildOptions.MaxCharterNameLength) return name;
        }

        return "Swift Foxes"; // unreachable in practice: 16 draws of 4 patterns all over 24 characters
    }

    /// <summary>Screen <paramref name="name"/> with every check, in the documented order; the first refusal is the verdict.</summary>
    internal static PlayerbotGuildNameVerdict Screen(PlayerbotGuildNameRules rules, string name)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(name);
        if (!CharterNameRules.IsValid(name, rules.Options, rules.Blacklist) || !LettersAndSpaces(name)) return PlayerbotGuildNameVerdict.Invalid;
        if (rules.IsTaken(name)) return PlayerbotGuildNameVerdict.Taken;
        if (rules.HoldsPetition(name)) return PlayerbotGuildNameVerdict.PetitionHeld;
        if (rules.Antispam?.IsSpam(name) == true) return PlayerbotGuildNameVerdict.Spam;
        if (rules.Catalog is { } catalog)
        {
            if (catalog.Check(name) != NameCatalogResult.Allowed) return PlayerbotGuildNameVerdict.NameCatalog;
            foreach (string word in name.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (catalog.Check(word) != NameCatalogResult.Allowed) return PlayerbotGuildNameVerdict.NameCatalog;
        }

        if (rules.WordFilter is { } filter && !string.Equals(filter.Apply(name), name, StringComparison.Ordinal)) return PlayerbotGuildNameVerdict.WordFilter;
        if (rules.Safety is { } safety)
        {
            SafetyVerdict verdict = safety.Screen(name, PlayerbotChatSafetyCategories.All);
            if (verdict.Flagged || verdict.TimedOut) return PlayerbotGuildNameVerdict.Unsafe;
        }

        return PlayerbotGuildNameVerdict.Ok;
    }

    /// <summary>The first of <see cref="MaxAttempts"/> generated names that passes <see cref="Screen"/>; null when none does (no guild is founded).</summary>
    internal static string? TryGenerate(PlayerbotGuildNameRules rules, Random random)
    {
        ArgumentNullException.ThrowIfNull(rules);
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            string name = Generate(random);
            if (Screen(rules, name) == PlayerbotGuildNameVerdict.Ok) return name;
        }

        return null;
    }

    private static string Pick(string[] words, Random random) => words[random.Next(words.Length)];

    private static bool LettersAndSpaces(string name)
    {
        if (name.Length == 0 || name[0] == ' ' || name[^1] == ' ' || name.Contains("  ", StringComparison.Ordinal)) return false;
        foreach (char c in name)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or ' ')) return false;
        return true;
    }
}
