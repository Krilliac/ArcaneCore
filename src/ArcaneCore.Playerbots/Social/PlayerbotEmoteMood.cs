namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>How a bot takes a text emote aimed at it.</summary>
internal enum PlayerbotEmoteMood : byte
{
    /// <summary>Not worth remembering (most emotes).</summary>
    Neutral,

    /// <summary>A wave, a bow, thanks, a cheer: raises affinity.</summary>
    Friendly,

    /// <summary>A rude gesture, a threat, a taunt: lowers affinity.</summary>
    Rude,

    /// <summary>A call for help (/helpme). Not a mood: it is what a bot in a losing fight asks with and what an answering bot reacts to.</summary>
    Help,
}

/// <summary>
/// Classifies a text emote id (EmotesText.dbc, vmangos TextEmotes) by a small static table. Emotes are the one channel that crosses factions,
/// so the classes are deliberately few and plain.
/// </summary>
internal static class PlayerbotEmoteMoods
{
    /// <summary>TEXTEMOTE_HELPME.</summary>
    internal const uint HelpMe = 303;

    internal static PlayerbotEmoteMood Classify(uint textEmote) => textEmote switch
    {
        // wave, bow, hello, greet, hail, cheer, thank, hug, salute, applaud, clap, golf clap, kiss, smile, dance, congratulate, welcome, curtsey, commend, praise, cuddle, love
        101 or 17 or 55 or 48 or 53 or 21 or 97 or 56 or 78 or 5 or 24 or 343 or 58 or 163 or 34 or 26 or 102 or 33 or 243 or 122 or 111 or 225
            => PlayerbotEmoteMood.Friendly,
        // rude, spit, chicken, threaten, insult, mock, violin, no, slap
        77 or 89 or 22 or 98 or 113 or 119 or 143 or 66 or 130 => PlayerbotEmoteMood.Rude,
        HelpMe => PlayerbotEmoteMood.Help,
        _ => PlayerbotEmoteMood.Neutral,
    };
}
