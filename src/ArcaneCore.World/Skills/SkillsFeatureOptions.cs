using ArcaneCore.Game.Skills;

namespace ArcaneCore.World.Skills;

/// <summary>Which skill implementation the daemon runs.</summary>
public enum SkillsMode
{
    /// <summary>
    /// The vmangos skill system: players carry real skills (SkillLine, SkillRaceClassInfo, SkillTiers and
    /// SkillLineAbility from the developer's build-5875 DBC files), gated by <see cref="SkillsFeatureOptions"/>.
    /// A configured file that is unreadable or has the wrong layout refuses startup. With no file configured
    /// at all the daemon logs an error banner and falls back to the stand-ins of <see cref="Legacy"/>.
    /// </summary>
    Retail = 0,

    /// <summary>
    /// The stand-ins from before the skill system (every skill reads 300, nobody can dual wield, trainers see
    /// no skill): development hosts only. Selected explicitly or when Retail has no DBC files to read.
    /// </summary>
    Legacy = 1,
}

/// <summary>
/// The "Skills" configuration section (docs/areas/skills.md). Every gain default is the vmangos default, see
/// <see cref="SkillOptions"/>.
/// </summary>
public sealed class SkillsFeatureOptions
{
    public const string SectionName = "Skills";

    public SkillsMode Mode { get; set; } = SkillsMode.Retail;

    /// <summary>Build-5875 SkillLine.dbc (22 fields).</summary>
    public string? SkillLineDbcPath { get; set; }

    /// <summary>Build-5875 SkillRaceClassInfo.dbc (8 fields).</summary>
    public string? SkillRaceClassInfoDbcPath { get; set; }

    /// <summary>Build-5875 SkillTiers.dbc (33 fields).</summary>
    public string? SkillTiersDbcPath { get; set; }

    /// <summary>Build-5875 SkillLineAbility.dbc (15 fields; 14 is also read).</summary>
    public string? SkillLineAbilityDbcPath { get; set; }

    /// <summary>vmangos AlwaysMaxSkillForLevel (default false).</summary>
    public bool AlwaysMaxSkillForLevel { get; set; }

    /// <summary>vmangos MaxPrimaryTradeSkill (default 2).</summary>
    public int MaxPrimaryTradeSkill { get; set; } = 2;

    /// <summary>vmangos SkillGain.Crafting (default 1).</summary>
    public uint GainCrafting { get; set; } = 1;

    /// <summary>vmangos SkillGain.Defense (default 1).</summary>
    public uint GainDefense { get; set; } = 1;

    /// <summary>vmangos SkillGain.Gathering (default 1).</summary>
    public uint GainGathering { get; set; } = 1;

    /// <summary>vmangos SkillGain.Weapon (default 1).</summary>
    public uint GainWeapon { get; set; } = 1;

    /// <summary>vmangos SkillChance.Orange (default 100).</summary>
    public uint ChanceOrange { get; set; } = 100;

    /// <summary>vmangos SkillChance.Yellow (default 75).</summary>
    public uint ChanceYellow { get; set; } = 75;

    /// <summary>vmangos SkillChance.Green (default 25).</summary>
    public uint ChanceGreen { get; set; } = 25;

    /// <summary>vmangos SkillChance.Grey (default 0).</summary>
    public uint ChanceGrey { get; set; }

    /// <summary>vmangos SkillChance.MiningSteps (code default 75; the sample configuration says 0 = no decay).</summary>
    public uint MiningSteps { get; set; } = 75;

    /// <summary>vmangos SkillChance.SkinningSteps (code default 75).</summary>
    public uint SkinningSteps { get; set; } = 75;

    /// <summary>How long a changed skill table waits before it is written (ms); logout, shutdown and deletion flush at once.</summary>
    public int FlushDebounceMs { get; set; } = 5000;

    /// <summary>The Game-layer tuning, with <paramref name="maxPlayerLevel"/> from the progression options.</summary>
    public SkillOptions ToSkillOptions(uint maxPlayerLevel) => new SkillOptions
    {
        AlwaysMaxSkillForLevel = AlwaysMaxSkillForLevel,
        MaxPrimaryTradeSkill = MaxPrimaryTradeSkill,
        GainCrafting = GainCrafting,
        GainDefense = GainDefense,
        GainGathering = GainGathering,
        GainWeapon = GainWeapon,
        ChanceOrange = ChanceOrange,
        ChanceYellow = ChanceYellow,
        ChanceGreen = ChanceGreen,
        ChanceGrey = ChanceGrey,
        MiningSteps = MiningSteps,
        SkinningSteps = SkinningSteps,
        MaxPlayerLevel = maxPlayerLevel,
    }.Validate();

    /// <summary>The four DBC paths, in the order SkillLine, SkillRaceClassInfo, SkillTiers, SkillLineAbility.</summary>
    public string?[] DbcPaths => [SkillLineDbcPath, SkillRaceClassInfoDbcPath, SkillTiersDbcPath, SkillLineAbilityDbcPath];
}
