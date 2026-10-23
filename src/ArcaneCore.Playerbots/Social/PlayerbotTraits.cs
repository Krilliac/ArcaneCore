namespace ArcaneCore.World.Playerbots.BotSocial;

/// <summary>
/// A bot's stable temperament, each trait 0..1 and derived from its guid alone (no state, no allocation), so it is the same after a
/// restart. Sociability is how much it seeks company, Chattiness how readily it talks, Generosity how freely it gives, Curiosity how
/// much it explores others and Helpfulness how readily it lends a hand.
/// </summary>
internal readonly record struct PlayerbotTraits(float Sociability, float Chattiness, float Generosity, float Curiosity, float Helpfulness)
{
    /// <summary>The traits of the bot with this guid: a hash of the guid with a distinct salt per trait (see <see cref="PlayerbotContention.Mix"/>).</summary>
    internal static PlayerbotTraits For(ulong botGuid) => new(
        Unit(botGuid, 0x50C1AB1E5A17UL),
        Unit(botGuid, 0x0C4A77E45A18UL),
        Unit(botGuid, 0x6E4E205177A19UL),
        Unit(botGuid, 0x0C0510517A1AUL),
        Unit(botGuid, 0x04E1F001A1BUL));

    /// <summary>A coarse band of a 0..1 trait: 0 low, 1 middle, 2 high.</summary>
    internal static int Band(float trait) => trait < 0.34f ? 0 : trait < 0.67f ? 1 : 2;

    private static float Unit(ulong botGuid, ulong salt)
        => (float)((PlayerbotContention.Mix(botGuid, salt) >> 11) * (1.0 / 9007199254740992.0));
}
