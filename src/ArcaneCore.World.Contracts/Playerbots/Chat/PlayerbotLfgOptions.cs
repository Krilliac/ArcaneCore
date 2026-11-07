namespace ArcaneCore.World.Playerbots.Chat;

/// <summary>
/// <c>World:Playerbots:Chat:Lfg</c>: how bots answer a real player's whispered "inv" to a bot's LFG or LFM call (docs/areas/playbots.md,
/// Bot voice). Every answer states only what the group coordinator knows; the built-in templates or a configured model only phrase it.
/// Every key is live (<c>.reload config</c>) and read at each heard line.
/// </summary>
public sealed class PlayerbotLfgOptions
{
    /// <summary>
    /// Let any bot of a waiting group answer a whisper "inv" for the group, not only the bot that posted the call (on by default). Off: only
    /// the whisper to the poster itself is answered.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Seconds within which a player's repeated "inv" to the same waiting group is applied without a second reply (10..600).</summary>
    public int AnswerRepeatSeconds { get; set; } = 60;

    /// <summary>The most (player, group) answers remembered for <see cref="AnswerRepeatSeconds"/> (8..512).</summary>
    public int MaxTrackedVolunteers { get; set; } = 64;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Chat:Lfg";
        Check(AnswerRepeatSeconds, 10, 600, nameof(AnswerRepeatSeconds));
        Check(MaxTrackedVolunteers, 8, 512, nameof(MaxTrackedVolunteers));

        static void Check(int value, int min, int max, string name)
        {
            if (value < min || value > max) throw new InvalidOperationException($"{section}: {name} must be {min}..{max}.");
        }
    }
}
