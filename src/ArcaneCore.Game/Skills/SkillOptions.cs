namespace ArcaneCore.Game.Skills;

/// <summary>
/// Skill gain tuning. Every default is the vmangos default (World.cpp:705-720 <c>LoadConfigSettings</c> and
/// mangosd.conf.dist.in:1319, 1340): the shipped sample configuration also lists
/// <c>SkillChance.MiningSteps = 0</c> / <c>SkinningSteps = 0</c> (:2842-2843) which switches the retail
/// decay of mining and skinning skill-ups off; the code default 75 reproduces the retail behaviour its own
/// comment describes (Player.cpp:5262: "1-74 - no decrease, 75-149 - 2 times, 225-299 - 8 times").
/// </summary>
public sealed record SkillOptions
{
    /// <summary>vmangos CONFIG_BOOL_ALWAYS_MAX_SKILL_FOR_LEVEL (default false): level-dependent skills always sit at their maximum.</summary>
    public bool AlwaysMaxSkillForLevel { get; init; }

    /// <summary>vmangos CONFIG_UINT32_MAX_PRIMARY_TRADE_SKILL (default 2).</summary>
    public int MaxPrimaryTradeSkill { get; init; } = 2;

    /// <summary>vmangos SkillGain.Crafting (default 1).</summary>
    public uint GainCrafting { get; init; } = 1;

    /// <summary>vmangos SkillGain.Defense (default 1).</summary>
    public uint GainDefense { get; init; } = 1;

    /// <summary>vmangos SkillGain.Gathering (default 1).</summary>
    public uint GainGathering { get; init; } = 1;

    /// <summary>vmangos SkillGain.Weapon (default 1).</summary>
    public uint GainWeapon { get; init; } = 1;

    /// <summary>vmangos SkillChance.Orange (percent, default 100).</summary>
    public uint ChanceOrange { get; init; } = 100;

    /// <summary>vmangos SkillChance.Yellow (default 75).</summary>
    public uint ChanceYellow { get; init; } = 75;

    /// <summary>vmangos SkillChance.Green (default 25).</summary>
    public uint ChanceGreen { get; init; } = 25;

    /// <summary>vmangos SkillChance.Grey (default 0).</summary>
    public uint ChanceGrey { get; init; }

    /// <summary>vmangos SkillChance.MiningSteps (code default 75, 0 = no decay).</summary>
    public uint MiningSteps { get; init; } = 75;

    /// <summary>vmangos SkillChance.SkinningSteps (code default 75, 0 = no decay).</summary>
    public uint SkinningSteps { get; init; } = 75;

    /// <summary>vmangos MaxPlayerLevel (default 60): the world constant behind <see cref="SkillRules.ConfigMaxSkillValue"/>.</summary>
    public uint MaxPlayerLevel { get; init; } = 60;

    /// <summary>The value a bad configuration must not reach the rules with; throws <see cref="ArgumentException"/> naming the option.</summary>
    public SkillOptions Validate()
    {
        if (MaxPrimaryTradeSkill is < 0 or > 10)
        {
            throw new ArgumentException("MaxPrimaryTradeSkill must be 0..10", nameof(MaxPrimaryTradeSkill));
        }

        foreach ((string name, uint value) in new[] { ("ChanceOrange", ChanceOrange), ("ChanceYellow", ChanceYellow), ("ChanceGreen", ChanceGreen), ("ChanceGrey", ChanceGrey) })
        {
            if (value > 100)
            {
                throw new ArgumentException($"{name} is a percentage (0..100)", name);
            }
        }

        foreach ((string name, uint value) in new[] { ("GainCrafting", GainCrafting), ("GainDefense", GainDefense), ("GainGathering", GainGathering) })
        {
            if (value == 0)
            {
                throw new ArgumentException($"{name} must be positive", name);
            }
        }

        if (MaxPlayerLevel is < 1 or > 100)
        {
            throw new ArgumentException("MaxPlayerLevel must be 1..100", nameof(MaxPlayerLevel));
        }

        return this;
    }
}
