namespace ArcaneCore.Game.Spells.Druid;

/// <summary>
/// Frenzied Regeneration's periodic conversion (D:\refs\vmangos\src\game\Spells\SpellAuras.cpp:1417-1433,
/// spell ids 22842, 22895, 22896; heal spell 22845). Each tick consumes up to 100 stored rage (10 rage; rage is
/// stored x10) and heals stored * amount / 10 where amount is the aura amount (10/15/20 for ranks 1-3). The source
/// dithers the float result to an integer (rand_dither); this returns the exact float.
/// </summary>
public static class FrenziedRegenerationRules
{
    public const uint HealSpell = 22845;
    public const uint MaxStoredRagePerTick = 100;

    public static (uint RageConsumed, float Heal) Tick(uint storedRage, int lifePerRage)
    {
        uint consumed = Math.Min(storedRage, MaxStoredRagePerTick);
        return (consumed, consumed * (float)lifePerRage / 10);
    }
}
