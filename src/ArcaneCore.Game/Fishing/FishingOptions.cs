namespace ArcaneCore.Game.Fishing;

/// <summary>
/// The fishing switches of vmangos World.cpp:718-720 (mangosd.conf SkillFail.*); every default is the retail behaviour.
/// </summary>
public sealed class FishingOptions
{
    /// <summary>
    /// <c>SkillFail.Loot.Fishing</c> (default false): a failed catch still opens the junk loot (fishing_loot_template entry 0).
    /// Retail 1.12 shows "fish escaped" instead.
    /// </summary>
    public bool FailLoot { get; set; }

    /// <summary><c>SkillFail.Gain.Fishing</c> (default false): a failed catch still raises the fishing skill.</summary>
    public bool FailGain { get; set; }

    /// <summary>
    /// <c>SkillFail.Possible.FishingPool</c> (default true). Counter-intuitively named: only when this is FALSE does vmangos search for a
    /// fishing hole after a failed roll and turn it into a success (GameObject.cpp:1672-1680); when true (default) a hole is only looked up
    /// after a successful roll.
    /// </summary>
    public bool FailPossibleFishingPool { get; set; } = true;
}