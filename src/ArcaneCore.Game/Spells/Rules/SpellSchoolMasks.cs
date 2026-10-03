namespace ArcaneCore.Game.Spells.Rules;

/// <summary>School bit masks (vmangos SpellSchoolMask: <c>1 &lt;&lt; school</c>).</summary>
public static class SpellSchoolMasks
{
    /// <summary>All seven schools (vmangos SPELL_SCHOOL_MASK_ALL).</summary>
    public const uint All = 0x7F;

    /// <summary>Everything but physical (vmangos SPELL_SCHOOL_MASK_MAGIC).</summary>
    public const uint Magic = 0x7E;

    public static uint Of(SpellSchool school) => 1u << (int)school;

    /// <summary>vmangos GetFirstSchoolInMask: the lowest school whose bit is set (Normal for an empty mask).</summary>
    public static SpellSchool FirstSchoolIn(uint mask)
    {
        for (int school = 0; school <= (int)SpellSchool.Arcane; school++)
        {
            if ((mask & (1u << school)) != 0)
            {
                return (SpellSchool)school;
            }
        }

        return SpellSchool.Normal;
    }
}
