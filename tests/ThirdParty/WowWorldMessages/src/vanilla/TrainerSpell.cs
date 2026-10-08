using WowSrp.Header;

namespace WowWorldMessages.Vanilla;

[System.CodeDom.Compiler.GeneratedCode("WoWM", "0.1.0")]
public class TrainerSpell {
    /// <summary>
    /// cmangos: learned spell (or cast-spell in profession case)
    /// </summary>
    public required uint Spell { get; set; }
    public required Vanilla.TrainerSpellState State { get; set; }
    public required uint SpellCost { get; set; }
    /// <summary>
    /// cmangos: spells don't cost talent points
    /// cmangos: set to 0
    /// </summary>
    public required uint TalentPointCost { get; set; }
    /// <summary>
    /// cmangos: must be equal prev. field to have learn button in enabled state
    /// cmangos: 1 for true 0 for false
    /// </summary>
    public required uint FirstRank { get; set; }
    public required byte RequiredLevel { get; set; }
    public required Vanilla.Skill RequiredSkill { get; set; }
    public required uint RequiredSkillValue { get; set; }
    public const int RequiredSpellsLength = 3;
    public required uint[] RequiredSpells { get; set; }

    public async Task WriteBodyAsync(Stream w, CancellationToken cancellationToken = default) {
        await w.WriteUInt(Spell, cancellationToken).ConfigureAwait(false);

        await w.WriteByte((byte)State, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(SpellCost, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(TalentPointCost, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(FirstRank, cancellationToken).ConfigureAwait(false);

        await w.WriteByte(RequiredLevel, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt((uint)RequiredSkill, cancellationToken).ConfigureAwait(false);

        await w.WriteUInt(RequiredSkillValue, cancellationToken).ConfigureAwait(false);

        foreach (var v in RequiredSpells) {
            await w.WriteUInt(v, cancellationToken).ConfigureAwait(false);
        }

    }

    public static async Task<TrainerSpell> ReadBodyAsync(Stream r, CancellationToken cancellationToken = default) {
        var spell = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var state = (Vanilla.TrainerSpellState)await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var spellCost = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var talentPointCost = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var firstRank = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredLevel = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        var requiredSkill = (Vanilla.Skill)await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredSkillValue = await r.ReadUInt(cancellationToken).ConfigureAwait(false);

        var requiredSpells = new uint[RequiredSpellsLength];
        for (var i = 0; i < RequiredSpellsLength; ++i) {
            requiredSpells[i] = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        return new TrainerSpell {
            Spell = spell,
            State = state,
            SpellCost = spellCost,
            TalentPointCost = talentPointCost,
            FirstRank = firstRank,
            RequiredLevel = requiredLevel,
            RequiredSkill = requiredSkill,
            RequiredSkillValue = requiredSkillValue,
            RequiredSpells = requiredSpells,
        };
    }

}

