using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Game.Spells;

public sealed partial class SpellSystem
{
    /// <summary>SkillLineAbility.dbc spell-rank links supplied by the world skills feature.</summary>
    public SpellRankChains RankChains { get; set; } = SpellRankChains.Empty;

    /// <summary>Find the highest learned rank in a forward chain, as vmangos Player::LookupHighestLearnedRank does.</summary>
    public uint HighestKnownRank(Player player, uint firstRank)
    {
        ArgumentNullException.ThrowIfNull(player);
        uint highest = 0;
        uint current = firstRank;
        for (int depth = 0; current != 0 && depth < 30; depth++)
        {
            if (Spellbook?.HasSpell(player, current) == true)
                highest = current;
            current = RankChains.Next(current);
        }

        return highest;
    }
}
