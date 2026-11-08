namespace ArcaneCore.Data.World.Creatures;

/// <summary>ClassicDB spell_script_target: a spell's type and entry filter for implicit script targets.</summary>
public sealed class SpellScriptTargetRow
{
    public uint SpellId { get; set; }
    public uint Type { get; set; }
    public uint TargetEntry { get; set; }
    public uint InverseEffectMask { get; set; }

    /// <summary>
    /// The condition the target must satisfy, translated from the vmangos <c>conditionId</c> at import (the term list of <see cref="SpellScriptTargetConditionTranslator"/>);
    /// empty is none. Condition ids are per database, so the id itself is never stored. Added by <see cref="SpellScriptTargetConditionDataModule"/>.
    /// </summary>
    public string TargetCondition { get; set; } = string.Empty;
}
