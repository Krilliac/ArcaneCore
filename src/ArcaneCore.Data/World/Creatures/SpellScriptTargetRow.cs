namespace ArcaneCore.Data.World.Creatures;

/// <summary>ClassicDB spell_script_target: a spell's type and entry filter for implicit script targets.</summary>
public sealed class SpellScriptTargetRow
{
    public uint SpellId { get; set; }
    public uint Type { get; set; }
    public uint TargetEntry { get; set; }
    public uint InverseEffectMask { get; set; }
}
