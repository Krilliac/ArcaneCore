using ArcaneCore.Game.Entities;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Talents;

/// <summary>
/// The spell rank chain talents need to find the higher ranks of a talent ability a player holds (vmangos
/// SpellMgr::GetSpellChainNext, used by Player::RemoveSpell :3820-3827). Only the previous-rank link is required: the
/// service walks the player's own spells upward, so no reverse index is needed.
/// </summary>
public interface IRankChain
{
    /// <summary>The previous rank of <paramref name="spellId"/>, 0 when it has none.</summary>
    uint PreviousRank(uint spellId);
}

/// <summary>
/// <see cref="IRankChain"/> over SkillLineAbility.dbc forward links (<see cref="SkillLineAbilityCatalog.PreviousRank"/>).
/// vmangos reads spell_chain instead; the two agree for trainer-learnable ranks (the only ranks a player can hold above a
/// talent), but a rank outside SkillLineAbility.dbc is not seen here and would stay learned after a respec.
/// </summary>
public sealed class SkillLineRankChain(SkillLineAbilityCatalog abilities) : IRankChain
{
    public uint PreviousRank(uint spellId) => abilities.PreviousRank(spellId);
}

/// <summary>Where the talent service asks for its changes to be persisted (the world daemon supplies it).</summary>
public interface ITalentSink
{
    /// <summary>The respec multiplier or time changed (a paid reset, or a price read that decayed it).</summary>
    void RespecChanged(Player player, RespecState state);

    /// <summary>A spell became disabled (hidden by a talent removal) or was re-enabled.</summary>
    void DisabledChanged(Player player, uint spellId, bool disabled);

    /// <summary>The character row changed (the respec price was paid): request a save.</summary>
    void CharacterChanged(Player player);
}
