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

    /// <summary>
    /// Path of the developer-supplied build-5875 Talent.dbc (never committed or downloaded). Unset together with
    /// <see cref="TalentTabDbcPath"/>: the talent system is inert (no points, the unlearn option stays hidden,
    /// CMSG_LEARN_TALENT is ignored) and a warning is logged. One set without the other, or an unreadable or
    /// invalid file, fails startup.
    /// </summary>
    public string? TalentDbcPath { get; set; }

    /// <summary>Path of the developer-supplied TalentTab.dbc (see <see cref="TalentDbcPath"/>).</summary>
    public string? TalentTabDbcPath { get; set; }

    /// <summary>
    /// Whether the wipe confirmation requires a class trainer of the player's own class (mangos-classic SkillHandler.cpp:51).
    /// vmangos checks only that the NPC is a reachable trainer (SkillHandler.cpp:37-56); the gossip offer, which both
    /// references share, already enforces the class, so a genuine client never notices. Default true (fail closed); false
    /// matches vmangos exactly.
    /// </summary>
    public bool RequireClassTrainerForWipe { get; set; } = true;

    /// <summary>
    /// After a refused reset (nothing spent, or not enough money) also send the empty confirmation that means "you have not
    /// spent any talent points" (vmangos SkillHandler.cpp:50-53). Not verified against a real client; false sends only the
    /// SMSG_BUY_FAILED of the money case.
    /// </summary>
    public bool WipeRefusalAlsoSendsEmptyConfirm { get; set; } = true;

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
