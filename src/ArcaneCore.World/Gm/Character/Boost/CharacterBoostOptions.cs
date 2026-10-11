using Microsoft.Extensions.Configuration;

namespace ArcaneCore.World.Gm.Character.Boost;

/// <summary>
/// The <c>World:GmCommands:Boost</c> configuration section of <c>.character boost</c> (an ArcaneCore operator command; vmangos has no
/// boost, so the defaults are ArcaneCore choices, documented in docs/areas/character-boost.md). Read when the command runs.
/// </summary>
public sealed class CharacterBoostOptions
{
    public const string SectionName = "World:GmCommands:Boost";

    /// <summary>
    /// The highest item quality a boost kit piece may have: 1 white, 2 green, 3 blue (default), 4 epic. Blue is what a character gets from
    /// the dungeons of its level; values outside 1-4 are clamped. White is always the lowest: grey items are never offered.
    /// </summary>
    public uint MaxQuality { get; set; } = BoostGearPlanner.DefaultMaxQuality;

    /// <summary><see cref="MaxQuality"/> clamped to 1-4.</summary>
    internal uint EffectiveMaxQuality => Math.Clamp(MaxQuality, 1u, 4u);

    /// <summary>Binds the section from <paramref name="configuration"/> (defaults when it is null or the section is absent).</summary>
    public static CharacterBoostOptions Bind(IConfiguration? configuration)
    {
        var options = new CharacterBoostOptions();
        configuration?.GetSection(SectionName).Bind(options);
        return options;
    }
}
