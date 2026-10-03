using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Game.Npc;

/// <summary>
/// Trainer spell knowledge over the existing spell system and spellbook (vmangos
/// ObjectMgr::LoadTrainers learnedSpell, Player::IsSpellFitByClassAndRace, SpellMgr spell
/// chains, Spell::EffectLearnSpell). Rank prerequisites and race/class fit come from
/// SkillLineAbility.dbc when supplied.
/// </summary>
/// <remarks>
/// There is no skills owner yet: every skill value reads 0, so trainer rows with a required
/// skill stay red, and no primary-profession points exist (profession first ranks are not gated
/// by points). spell_chain "req_spell" requirements (talent-dependent ranks) are not modelled.
/// </remarks>
/// <param name="spells">The spell system (resolved lazily: features attach in name order).</param>
/// <param name="abilities">SkillLineAbility.dbc data (empty: no rank chain, every spell fits).</param>
public sealed class SpellSystemLearner(Func<SpellSystem?> spells, SkillLineAbilityCatalog abilities) : ISpellLearner
{
    public TrainerSpellInfo? DescribeTrainerSpell(uint teachingSpell)
    {
        if (spells()?.Store is not { } store || store.Get(teachingSpell) is not { } teaching)
        {
            return null;
        }

        uint learned = LearnedSpells(teaching).FirstOrDefault();
        if (learned == 0 || store.Get(learned) is not { } learnedInfo)
        {
            return null;
        }

        return new TrainerSpellInfo(learned, learnedInfo.SpellLevel, abilities.PreviousRank(learned), 0, false, false, false);
    }

    public bool HasSpell(Player player, uint spellId) => spells()?.Spellbook?.HasSpell(player, spellId) ?? false;

    public bool IsSpellFitByClassAndRace(Player player, uint spellId)
        => abilities.FitsClassAndRace(spellId, Mask((byte)player.Race), Mask((byte)player.Class));

    public uint GetSkillValueBase(Player player, uint skill) => 0;

    public uint GetSkillValue(Player player, uint skill) => 0;

    public uint GetFreePrimaryProfessionPoints(Player player) => 0;

    /// <summary>
    /// vmangos HandleTrainerBuySpellOpcode: a teaching spell with SPELL_EFFECT_LEARN_SPELL teaches
    /// its trigger spells (Spell::EffectLearnSpell), any other is learned itself.
    /// </summary>
    public bool CastTeachingSpell(Player player, ObjectGuid trainer, uint teachingSpell)
    {
        if (spells() is not { } system || system.Store.Get(teachingSpell) is not { } teaching)
        {
            return false;
        }

        bool learnedAny = false;
        foreach (uint spell in LearnedSpells(teaching))
        {
            learnedAny |= system.LearnSpell(player, spell);
        }

        return learnedAny;
    }

    public void CastQuestRewardSpell(Player player, ObjectGuid questEnder, uint spellId)
        => spells()?.CastSpell(player, spellId, SpellCastTargets.ForSelf(), triggered: true);

    private static IEnumerable<uint> LearnedSpells(SpellInfo teaching)
    {
        uint[] learned = teaching.Effects
            .Where(e => e.Effect == SpellEffectName.LearnSpell && e.TriggerSpell != 0)
            .Select(e => e.TriggerSpell)
            .ToArray();
        return learned.Length > 0 ? learned : [teaching.Id];
    }

    private static uint Mask(byte id) => id is > 0 and <= 32 ? 1u << (id - 1) : 0;
}
