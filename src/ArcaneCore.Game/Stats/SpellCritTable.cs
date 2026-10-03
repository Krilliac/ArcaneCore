namespace ArcaneCore.Game.Stats;

/// <summary>
/// Spell crit from intellect: <c>base + intellect / (rate0 + rate1 * level)</c> per class (vmangos
/// Unit.cpp:2597-2641; mangos-classic Player.cpp:4981-5015 carries the identical table).
/// <para>
/// NOT PROVEN RETAIL: both references mark this table with the comment "from mangos 3462 for 1.12 MUST BE
/// CHECKED" (vmangos Unit.cpp:2604, mangos-classic Player.cpp:4988), and mangos-classic adds "FIXME: Add
/// base value and scaling for hunters, fix the formula" (Player.cpp:5009). It is the best available
/// reference formula and is shipped as such; <see cref="VerifiedRetail"/> records that it is unverified.
/// </para>
/// </summary>
public static class SpellCritTable
{
    /// <summary>False: the reference marks the table unchecked against retail (see the type remarks).</summary>
    public const bool VerifiedRetail = false;

    /// <summary>Non-player units use a flat 5% spell crit (vmangos Unit.cpp:2635-2636).</summary>
    public const float NonPlayerSpellCrit = 5.0f;

    private readonly record struct Row(float Base, float Rate0, float Rate1);

    /// <summary>Unit.cpp:2611-2625. Classes without spell crit keep the (0, 0, 10) filler row.</summary>
    private static Row For(Class playerClass) => playerClass switch
    {
        Class.Paladin => new Row(3.70f, 14.77f, 0.65f),
        Class.Priest => new Row(2.97f, 10.03f, 0.82f),
        Class.Shaman => new Row(3.54f, 11.51f, 0.80f),
        Class.Mage => new Row(3.70f, 14.77f, 0.65f),
        Class.Warlock => new Row(3.18f, 11.30f, 0.82f),
        Class.Druid => new Row(3.33f, 12.41f, 0.79f),
        _ => new Row(0.0f, 0.0f, 10.0f),
    };

    /// <summary>
    /// Unit::GetSpellCritFromIntellect for a player (Unit.cpp:2629-2638). Values outside the playable
    /// classes (the reference's unused array rows) give 0.
    /// </summary>
    public static float CritFromIntellect(Class playerClass, uint level, float intellect)
    {
        if (!Enum.IsDefined(playerClass))
        {
            return 0.0f;
        }

        Row row = For(playerClass);
        float critRatio = row.Rate0 + (row.Rate1 * level);
        float critChance = row.Base + (intellect / critRatio);
        return critChance > 0.0 ? critChance : 0.0f;
    }
}
