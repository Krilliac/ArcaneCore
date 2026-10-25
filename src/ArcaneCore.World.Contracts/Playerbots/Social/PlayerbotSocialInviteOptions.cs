namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// <c>World:Playerbots:Social:Invites</c>: whether a real player may stop the bots from inviting them unasked (docs/areas/playbots-social.md,
/// Membership). A player who whispers a bot "invites off" is never sent an unsolicited group or guild invitation by any bot; asking for one
/// ("inv", "ginv") is still consent. Every key is live: <c>.reload config</c> changes this object in place and the parser reads it per line.
/// </summary>
public sealed class PlayerbotSocialInviteOptions
{
    /// <summary>
    /// Parse the whispered commands "invites off", "invites on" and "invites" (on by default). Off: the line is not a command and goes on to ordinary
    /// whisper handling. It never undoes an opt-out already recorded: a player who opted out stays out whatever this says.
    /// </summary>
    public bool AllowOptOut { get; set; } = true;

    public void Validate()
    {
    }
}
