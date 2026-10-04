namespace ArcaneCore.Game.Progression;

/// <summary>
/// Rested experience settings (configuration section "Rest"). The three rates are the mangosd.conf.dist values of
/// the same name, default 1: Rate.Rest.InGame, Rate.Rest.Offline.InTavernOrCity, Rate.Rest.Offline.InWilderness.
/// The size of the pool is not an option: the client doubles what it is sent, so the cap is fixed at one and a half
/// levels of experience (vmangos Player::SetRestBonus).
/// </summary>
public sealed class RestOptions
{
    public const string SectionName = "Rest";

    /// <summary>Rate.Rest.InGame: multiplier of the rested experience gained while resting in an inn or a capital city. 0 turns the gain off.</summary>
    public float RateInGame { get; set; } = 1.0f;

    /// <summary>Rate.Rest.Offline.InTavernOrCity: multiplier of the rested experience gained while logged out, when the character logged out resting.</summary>
    public float RateOfflineInTavernOrCity { get; set; } = 1.0f;

    /// <summary>
    /// Rate.Rest.Offline.InWilderness: multiplier of the rested experience gained while logged out, when the character
    /// did not log out resting. The gain is a quarter of the resting one at rate 1 (the reference divides it by four).
    /// </summary>
    public float RateOfflineInWilderness { get; set; } = 1.0f;

    /// <summary>
    /// Seconds of resting between two gains of rested experience (the reference adds the gain once at least 10 seconds have passed since
    /// the last one). The pool grows by the same amount per second whatever the interval; a longer one only makes the steps larger.
    /// Values below 1 count as 1.
    /// </summary>
    public uint AccrualIntervalSeconds { get; set; } = 10;

    /// <summary>
    /// Seconds between the writes of the rested state of every online character (the pool, the time and the resting flag; the time
    /// is what offline accrual counts from after a crash). A logout and a shutdown always write. 0 writes only then, so after a crash the
    /// stored time is that of the last logout and the whole session counts as offline time.
    /// </summary>
    public uint SaveIntervalSeconds { get; set; } = 300;
}
