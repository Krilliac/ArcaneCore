namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Helping</c>: bots answer calls for help and lend a hand to strangers (docs/areas/playbots-social.md, Helping).
/// An idle, healthy bot answers a targeted help emote or a help yell from a same-faction player or bot nearby by the P1 rule
/// (<c>PlayerbotCallForHelp.Answers</c>: its helpfulness scaled by how it feels about the asker), and on every social pass looks for a
/// same-faction player or bot in trouble (in a fight, below 40% health) within <see cref="TroubleRadiusYards"/>: a damage dealer attacks the
/// victim's attacker, a healer heals the victim, and out of combat a class that can resurrects a dead stranger or buffs one. Every action
/// goes through the bot's own packets (CMSG_ATTACKSWING, CMSG_CAST_SPELL). Every key is live.
/// </summary>
public sealed class PlayerbotSocialHelpingOptions
{
    /// <summary>The widest <see cref="TroubleRadiusYards"/>.</summary>
    public const float MaxTroubleRadiusYards = 100f;

    /// <summary>The longest <see cref="BuffCooldownMinutes"/>.</summary>
    public const int MaxBuffCooldownMinutes = 1440;

    /// <summary>Let bots look for strangers in trouble and help them (on by default). Off: no scan, and no call for help is answered either.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How far (yards) from an idle bot a unit in trouble may be and still be noticed (1..100).</summary>
    public float TroubleRadiusYards { get; set; } = 30f;

    /// <summary>Let a bot whose class can resurrect resurrect a dead same-faction stranger with a recent corpse, once per corpse (on by default).</summary>
    public bool ResurrectStrangers { get; set; } = true;

    /// <summary>Let a bot occasionally cast one of its class buffs (the ones it casts on groupmates) on a same-faction stranger (on by default).</summary>
    public bool BuffStrangers { get; set; } = true;

    /// <summary>The least minutes between two buffs a bot gives the same stranger (0..1440).</summary>
    public int BuffCooldownMinutes { get; set; } = 30;

    /// <summary>Let a bot that is asked for help (a targeted help emote, or a help yell or say from someone close) decide whether to answer it (on by default).</summary>
    public bool AnswerHelp { get; set; } = true;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Helping";
        if (!float.IsFinite(TroubleRadiusYards) || TroubleRadiusYards is < 1f or > MaxTroubleRadiusYards)
            throw new InvalidOperationException($"{section}: TroubleRadiusYards must be 1..{MaxTroubleRadiusYards}.");
        if (BuffCooldownMinutes is < 0 or > MaxBuffCooldownMinutes)
            throw new InvalidOperationException($"{section}: BuffCooldownMinutes must be 0..{MaxBuffCooldownMinutes}.");
    }
}
