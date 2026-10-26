using System.Globalization;

namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Rivalry</c>: how bots treat characters of the other faction (docs/areas/playbots-social.md, Rivalry). This stage
/// is emotes only, with no combat: an idle bot greets a stranger or a friend of the other faction with a friendly text emote, a bot that holds a
/// rival (the Rival flag of its social memory) answers with a rude one, and an emote aimed at a bot is answered by how the bot feels about the
/// emoter. A player can stop the taunts by whispering any bot of the player's own faction "rivals off". Every key is live: <c>.reload config</c>
/// changes this object in place and the lane reads it at every pass.
/// </summary>
public sealed class PlayerbotSocialRivalryOptions
{
    /// <summary>The master switch (on by default). Off: the lane sends nothing and an emote is answered as before the stance existed. Recorded opt-outs are kept.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Parse the whispered commands "rivals off", "rivals on" and "rivals" (on by default); they are also parsed while
    /// <see cref="PlayerbotSocialRivalryEmoteOptions.RudeRequiresOptIn"/> is on. Off: the line is not a command and goes on to ordinary whisper
    /// handling. It never undoes an opt-out already recorded: a player who opted out stays out whatever this says.
    /// </summary>
    public bool AllowOptOut { get; set; } = true;

    /// <summary>Greetings, rude emotes and the stance-aware replies (<c>World:Playerbots:Social:Rivalry:Emotes</c>).</summary>
    public PlayerbotSocialRivalryEmoteOptions Emotes { get; set; } = new();

    /// <summary>Places where no rude emote is sent (<c>World:Playerbots:Social:Rivalry:Safeguards</c>).</summary>
    public PlayerbotSocialRivalrySafeguardOptions Safeguards { get; set; } = new();

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Rivalry";
        if (Emotes is null) throw new InvalidOperationException($"{section}:Emotes is missing.");
        if (Safeguards is null) throw new InvalidOperationException($"{section}:Safeguards is missing.");
        Emotes.Validate();
        Safeguards.Validate();
    }
}

/// <summary>
/// <c>World:Playerbots:Social:Rivalry:Emotes</c>. Not <c>World:Playerbots:Social:Emotes</c>, which is the same-faction wave of lane "emotes": this
/// section is the cross-faction lane "rivalry". Toward another bot the lane and the replies read only the normal layer of the bot's memory, War
/// Mode or not; toward a real player the lane's own emotes read the layer the memory's decisions read.
/// </summary>
public sealed class PlayerbotSocialRivalryEmoteOptions
{
    /// <summary>Let idle bots greet characters of the other faction and send rude emotes at rivals (lane "rivalry"; on by default).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Send rude emotes at rivals and let a rival or a disliked character be answered rudely or not at all (on by default). Off: every reply is by a neutral stance.</summary>
    public bool RivalEmotes { get; set; } = true;

    /// <summary>
    /// Let rude emotes and rude replies reach other bots (off by default). Off: nothing the rivalry stage sends to a bot is ever rude, a rude
    /// emote aimed at a bot is met with a shrug or a sigh, so no exchange between two bots can lower an affinity.
    /// </summary>
    public bool RudeAtBots { get; set; }

    /// <summary>Send rude emotes only to real players who whispered "rivals on" (off by default: they go to every real rival who has not opted out).</summary>
    public bool RudeRequiresOptIn { get; set; }

    /// <summary>Let bots greet each other across factions when a real player is close enough to see it (on by default).</summary>
    public bool BotToBot { get; set; } = true;

    /// <summary>The affinity from which a bot regards a character as a friend (0..100; default 20).</summary>
    public int FriendlyAffinity { get; set; } = 20;

    /// <summary>
    /// How close, in yards, a character of the other faction must stand to be greeted or taunted (5..25; default 20). The range actually used is
    /// never more than <c>World:ListenRangeTextEmote</c>, which is read at every pass.
    /// </summary>
    public int RangeYards { get; set; } = 20;

    /// <summary>The chance (0..1, times the bot's sociability) that a bot greets a stranger of the other faction; a known neutral is greeted at half the bot's sociability, a friend always (default 0.25).</summary>
    public double GreetStrangerChance { get; set; } = 0.25;

    /// <summary>The least time, in minutes, between two rude emotes from one bot to one character (1..1440; default 30).</summary>
    public int RudePairGapMinutes { get; set; } = 30;

    /// <summary>The least time, in minutes, between two rude emotes at one character from all bots together (1..1440; default 30).</summary>
    public int RudePerTargetMinutes { get; set; } = 30;

    /// <summary>The most greetings and rude emotes the lane sends in a minute, from all bots, counted apart for bot targets and player targets (0..120; 0 silences the lane; default 12). Replies are not counted.</summary>
    public int EmotesPerMinute { get; set; } = 12;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Rivalry:Emotes";
        Check(FriendlyAffinity, 0, 100, nameof(FriendlyAffinity));
        Check(RangeYards, 5, 25, nameof(RangeYards));
        Check(RudePairGapMinutes, 1, 1440, nameof(RudePairGapMinutes));
        Check(RudePerTargetMinutes, 1, 1440, nameof(RudePerTargetMinutes));
        Check(EmotesPerMinute, 0, 120, nameof(EmotesPerMinute));
        if (!double.IsFinite(GreetStrangerChance) || GreetStrangerChance is < 0.0 or > 1.0)
            throw new InvalidOperationException($"{section}: {nameof(GreetStrangerChance)} must be 0..1.");

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}

/// <summary><c>World:Playerbots:Social:Rivalry:Safeguards</c>: where bots stay polite.</summary>
public sealed class PlayerbotSocialRivalrySafeguardOptions
{
    /// <summary>The area ids of the towns where both factions keep a guard post (Booty Bay, Ratchet, Gadgetzan, Everlook, Nighthaven, Light's Hope Chapel).</summary>
    public const string DefaultSafeAreas = "35,392,976,2255,2361,2268";

    private sealed record Parsed(string Text, IReadOnlySet<uint> Ids);

    // One reference, swapped whole: the parsed list is rebuilt only when the text is replaced (a reload), and a reader never sees half of one.
    private volatile Parsed? _parsed;

    /// <summary>
    /// Area ids (comma separated; the sub-zone a character stands in, not its zone) where no rude emote is sent, on top of every capital city, which
    /// is always quiet. The default is the six neutral towns with a guard post for both factions: 35 Booty Bay, 392 Ratchet, 976 Gadgetzan,
    /// 2255 Everlook, 2361 Nighthaven and 2268 Light's Hope Chapel. Empty keeps only the capitals.
    /// </summary>
    public string SafeAreas { get; set; } = DefaultSafeAreas;

    /// <summary>The ids of <see cref="SafeAreas"/>; an entry that is not an unsigned number is left out (<see cref="Validate"/> refuses the text).</summary>
    public IReadOnlySet<uint> SafeAreaIds()
    {
        string text = SafeAreas ?? string.Empty;
        if (_parsed is { } cached && ReferenceEquals(cached.Text, text)) return cached.Ids;
        var ids = new HashSet<uint>();
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) ids.Add(id);
        _parsed = new Parsed(text, ids);
        return ids;
    }

    /// <summary>Why <paramref name="text"/> is not a list of area ids, or null when it is.</summary>
    public static string? Check(string? text)
    {
        foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (!uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) return $"\"{part}\" is not an area id";
        return null;
    }

    public void Validate()
    {
        if (Check(SafeAreas) is { } problem)
            throw new InvalidOperationException($"{PlayerbotOptions.SectionName}:Social:Rivalry:Safeguards: SafeAreas must be a comma separated list of area ids ({problem}).");
    }
}
