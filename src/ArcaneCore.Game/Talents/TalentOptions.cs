namespace ArcaneCore.Game.Talents;

/// <summary>What the cost field of the "no talents spent" MSG_TALENT_WIPE_CONFIRM carries.</summary>
public enum TalentEmptyConfirmCost
{
    /// <summary>The current respec price (vmangos SendTalentWipeConfirm(Empty), Player.cpp:8259-8265).</summary>
    Current,

    /// <summary>Zero (mangos-classic SkillHandler.cpp writes uint64(0) and uint32(0)).</summary>
    Zero,
}

/// <summary>
/// Talent tuning (configuration section <see cref="Section"/>). Every default reproduces vmangos
/// (World.cpp:544-549 Rate.Talent, Rate.RespecBaseCost, Rate.RespecMultiplicativeCost,
/// Rate.RespecMinMultiplier, Rate.RespecMaxMultiplier; mangosd.conf.dist.in:540-542, :2668-2686);
/// a deviation is a switch whose default is retail.
/// </summary>
public sealed class TalentOptions
{
    public const string Section = "Talents";

    /// <summary>Multiplier on the talent points a level grants (vmangos Rate.Talent, default 1).</summary>
    public double PointsRate { get; set; } = 1.0;

    /// <summary>Gold charged by the first (or fully decayed) respec (vmangos Rate.RespecBaseCost, default 1).</summary>
    public uint RespecBaseCostGold { get; set; } = 1;

    /// <summary>Gold per multiplier step (vmangos Rate.RespecMultiplicativeCost, default 5).</summary>
    public uint RespecMultiplicativeCostGold { get; set; } = 5;

    /// <summary>Floor the decay never drops below once the multiplier had reached it (vmangos Rate.RespecMinMultiplier, default 2).</summary>
    public uint RespecMinMultiplier { get; set; } = 2;

    /// <summary>Cap of the multiplier (vmangos Rate.RespecMaxMultiplier, default 10: 50 gold).</summary>
    public uint RespecMaxMultiplier { get; set; } = 10;

    /// <summary>
    /// Respec price decays one step per elapsed 30-day month. Always on for build 5875: vmangos gates it with
    /// <c>!NoRespecPriceDecay || patch &gt;= 1.11</c> (Player.cpp:4056) and 5875 is patch 1.12. A switch for emulating
    /// earlier patches.
    /// </summary>
    public bool RespecPriceDecay { get; set; } = true;

    /// <summary>
    /// Deviation switch, off = vmangos. vmangos mutates the stored multiplier on every price read without advancing
    /// the last-respec time, so reading the price twice in a later month decays it twice (Player.cpp:4028-4051).
    /// When true the decay is applied once per elapsed month: it is computed from the stored state and only
    /// persisted by an actual respec.
    /// </summary>
    public bool IdempotentRespecDecay { get; set; }

    /// <summary>
    /// The cost field of the empty confirmation sent when a reset is refused (nothing spent, or not enough money). vmangos
    /// fills it with the current price; mangos-classic sends zero. Default = vmangos, the primary reference.
    /// </summary>
    public TalentEmptyConfirmCost EmptyConfirmCost { get; set; } = TalentEmptyConfirmCost.Current;
}
