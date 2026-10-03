namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Furor on a shift into cat or bear (D:\refs\vmangos\src\game\Spells\SpellAuras.cpp:2512-2548, build
/// branch after 1.11.2 where cat falls into the shared bear block). The chance is the amount of the player's
/// Dummy aura with SpellIconID 238; the roll is irand(1,100) &lt;= chance. Cat then casts 17099 (energy 40),
/// bear casts 17057 (rage 100 stored, i.e. 10 rage). Rank ids and amounts verified against classic-db:
/// 17056, 17058, 17059, 17060, 17061 = 20/40/60/80/100 (17057 and 17099 are the proc spells, not ranks).
/// </summary>
public static class FurorRules
{
    public const uint DummyIconId = 238;
    public const uint CatEnergySpell = 17099;
    public const uint BearRageSpell = 17057;

    public static readonly IReadOnlyDictionary<uint, int> RankAmounts = new Dictionary<uint, int>
    {
        [17056] = 20,
        [17058] = 40,
        [17059] = 60,
        [17060] = 80,
        [17061] = 100,
    };

    /// <summary>The proc spell for a shift into <paramref name="form"/>, or 0 (no proc for other forms).</summary>
    public static uint ProcSpell(byte form) => form switch
    {
        DruidForms.Cat => CatEnergySpell,
        DruidForms.Bear or DruidForms.DireBear => BearRageSpell,
        _ => 0,
    };

    /// <summary>irand(1, 100) &lt;= chance. <paramref name="roll"/> is the 1..100 draw.</summary>
    public static bool Procs(int chance, int roll) => roll <= chance;
}
