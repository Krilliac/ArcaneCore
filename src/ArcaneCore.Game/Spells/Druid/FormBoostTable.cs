namespace ArcaneCore.Game.Spells.Druid;

/// <summary>The permanent linked spells a form adds to its owner, plus its Heart of the Wild spell (0 = none).</summary>
public readonly record struct FormBoosts(uint Spell1, uint Spell2, uint HeartOfTheWildSpell);

/// <summary>
/// The spell ids vmangos Aura::HandleShapeshiftBoosts links to each druid form
/// (D:\refs\vmangos\src\game\Spells\SpellAuras.cpp:5433-5480), the Leader of the Pack rule (:5497-5503)
/// and the Heart of the Wild lookup constants (:5505-5530). Ids verified against classic-db spell_template:
/// 3025 Cat Form (Passive), 1178 Bear Form (Passive), 21178 Bear Form (Passive2), 9635 Dire Bear Form
/// (Passive), 5419 Travel Form (Passive), 5421 Aquatic Form (Passive), 5420 Tree Form (Passive) 2,
/// 24905 Moonkin Form (Passive), 24900/24899 Heart of the Wild Cat/Bear Effect, 24932 Leader of the Pack.
/// </summary>
public static class FormBoostTable
{
    public const uint LeaderOfThePackKnownSpell = 17007;
    public const uint LeaderOfThePackEffectSpell = 24932;

    /// <summary>SpellIconID of the Heart of the Wild talent auras (ModTotalStatPercentage, misc value 3 = intellect).</summary>
    public const uint HeartOfTheWildIconId = 240;

    public const int HeartOfTheWildMiscValue = 3;

    public static FormBoosts Get(byte form) => form switch
    {
        DruidForms.Cat => new FormBoosts(3025, 0, 24900),
        DruidForms.Tree => new FormBoosts(5420, 0, 0),
        DruidForms.Travel => new FormBoosts(5419, 0, 0),
        DruidForms.Aquatic => new FormBoosts(5421, 0, 0),
        DruidForms.Bear => new FormBoosts(1178, 21178, 24899),
        DruidForms.DireBear => new FormBoosts(9635, 21178, 24899),
        DruidForms.Moonkin => new FormBoosts(24905, 0, 0),
        _ => default,
    };

    /// <summary>
    /// Leader of the Pack (vmangos :5497-5503): cast when the player knows 17007 and the effect spell's
    /// Stances mask contains the form (Stances 0x91 = cat, bear, dire bear).
    /// </summary>
    public static bool LeaderOfThePackApplies(bool knowsTalent, uint effectSpellStances, byte form) =>
        knowsTalent && DruidForms.StanceMask(form) != 0 && (effectSpellStances & DruidForms.StanceMask(form)) != 0;
}
