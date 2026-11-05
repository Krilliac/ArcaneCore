namespace ArcaneCore.Data.World.Creatures;

/// <summary>ClassicDB spell_script_target: a spell's type and entry filter for implicit script targets.</summary>
public sealed class SpellScriptTargetRow
{
    public uint SpellId { get; set; }
    public uint Type { get; set; }
    public uint TargetEntry { get; set; }
    public uint InverseEffectMask { get; set; }

    /// <summary>The <c>conditions</c> row the target must satisfy (vmangos <c>conditionId</c>); 0 is none. Added by <see cref="SpellScriptTargetConditionDataModule"/>.</summary>
    public uint ConditionId { get; set; }
}
