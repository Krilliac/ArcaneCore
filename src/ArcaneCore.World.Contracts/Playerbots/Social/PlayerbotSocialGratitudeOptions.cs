namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Gratitude</c>: a bot thanks whoever helped it (docs/areas/playbots-social.md, Gratitude). Help is a rescue in a fight
/// (<c>HelpedBy</c> in the social memory), a resurrection or a buff from someone outside its group. The thanks is a <c>thank</c> text emote at the
/// helper (always, across factions too) and, for a same-faction helper and a chatty bot, a short line through the announcement path. A generous bot
/// (the Generosity trait) that likes the helper (affinity 30 or more) may also buff them, resurrect them later if they die nearby, or, rarely, tip
/// them a little gold through a real trade (CMSG_INITIATE_TRADE, CMSG_SET_TRADE_GOLD, CMSG_ACCEPT_TRADE). One thanks per helper per fight. Every key is live.
/// </summary>
public sealed class PlayerbotSocialGratitudeOptions
{
    /// <summary>The largest <see cref="MaxTipCopper"/> (100 gold).</summary>
    public const int MaxTipCopperLimit = 1_000_000;

    /// <summary>Let bots thank whoever helped them (on by default). Off: no emote, no line, no favour.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The chance (0..1), before the bot's Generosity trait scales it, that a thanks to a well-liked human helper is a gold tip by trade (default 0.05).</summary>
    public float TipChance { get; set; } = 0.05f;

    /// <summary>The most copper a tip may be for a level 60 bot (a lower level tips proportionally less; never more than 1% of the bot's gold). 0 turns tips off (default 10000, one gold).</summary>
    public int MaxTipCopper { get; set; } = 10_000;

    public void Validate()
    {
        const string section = PlayerbotOptions.SectionName + ":Social:Gratitude";
        if (!float.IsFinite(TipChance) || TipChance is < 0f or > 1f) throw new InvalidOperationException($"{section}: TipChance must be 0..1.");
        if (MaxTipCopper is < 0 or > MaxTipCopperLimit) throw new InvalidOperationException($"{section}: MaxTipCopper must be 0..{MaxTipCopperLimit}.");
    }
}
