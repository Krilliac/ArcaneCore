namespace ArcaneCore.Game.Talents;

/// <summary>The persisted respec economy of one character (vmangos characters.reset_talents_multiplier / reset_talents_time, Player.cpp:14901-14902).</summary>
public readonly record struct RespecState(uint Multiplier, long TimeUnix);

/// <summary>
/// The outcome of reading the respec price: the cost, the decayed state the price was computed from, and the
/// state to persist after the read (see <see cref="TalentOptions.IdempotentRespecDecay"/>).
/// </summary>
public readonly record struct RespecQuote(uint Cost, RespecState Effective, RespecState ToStore);

/// <summary>
/// The talent respec price. vmangos Player::UpdateResetTalentsMultiplier / GetResetTalentsCost
/// (src/game/Objects/Player.cpp:4028-4073) and the post-respec update at :4132-4142; MONTH = DAY * 30
/// (src/shared/Common.h:131).
/// </summary>
public static class RespecCost
{
    public const long MonthSeconds = 30L * 24 * 60 * 60;

    private const uint Gold = 10000;

    /// <summary>vmangos UpdateResetTalentsMultiplier: one step per elapsed month, floored at the minimum once the value had reached it. The time is not advanced.</summary>
    public static RespecState Decay(RespecState state, long nowUnix, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.RespecPriceDecay)
        {
            return state;
        }

        long months = (nowUnix - state.TimeUnix) / MonthSeconds;
        if (months <= 0)
        {
            return state;
        }

        uint multiplier = state.Multiplier;
        bool clamp = multiplier >= options.RespecMinMultiplier;
        multiplier = months > multiplier ? 0 : multiplier - (uint)months;
        if (clamp && multiplier < options.RespecMinMultiplier)
        {
            multiplier = options.RespecMinMultiplier;
        }

        return state with { Multiplier = multiplier };
    }

    /// <summary>The price in copper of a respec at <paramref name="multiplier"/> (vmangos GetResetTalentsCost, after the decay).</summary>
    public static uint Copper(uint multiplier, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (multiplier == 0)
        {
            return Saturate((UInt128)options.RespecBaseCostGold * Gold);
        }

        uint steps = Math.Min(multiplier, options.RespecMaxMultiplier);
        return Saturate((UInt128)options.RespecMultiplicativeCostGold * steps * Gold);
    }

    /// <summary>Read the price (applies the decay). <see cref="RespecQuote.ToStore"/> is what vmangos would now hold in memory.</summary>
    public static RespecQuote Quote(RespecState stored, long nowUnix, TalentOptions options)
    {
        RespecState effective = Decay(stored, nowUnix, options);
        RespecState toStore = options.IdempotentRespecDecay ? stored : effective;
        return new RespecQuote(Copper(effective.Multiplier, options), effective, toStore);
    }

    /// <summary>The state after a paid respec: multiplier + 1 capped at the maximum, time = now (Player.cpp:4132-4142).</summary>
    public static RespecState AfterRespec(RespecState effective, long nowUnix, TalentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        uint next = effective.Multiplier == uint.MaxValue ? uint.MaxValue : effective.Multiplier + 1;
        return new RespecState(Math.Min(next, options.RespecMaxMultiplier), nowUnix);
    }

    private static uint Saturate(UInt128 value) => value > uint.MaxValue ? uint.MaxValue : (uint)value;
}
