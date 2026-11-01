using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>The deliberately inert spells of Naxxramas: content lanes add their entries here, never in the shared files.</summary>
public static partial class DeliberatelyInertSpells
{
    private static InertSpell[] BuildNaxxramas() =>
    [
        new(29357, R.Visual, "Sapphiron Dies: the client plays a camera shake when Sapphiron falls; the DUMMY effect on himself has no reader",
            "vmangos boss_sapphiron.cpp:52 SPELL_SAPPHIRON_DIES ('adds camera-shake'), cast in JustDied :196; no core handles the effect"),
        new(28471, R.NeverCast, "Dispel Shackles (ClearAll): Kel'Thuzad's shackle clean-up is done by hand, the spell itself is never cast",
            "vmangos boss_kelthuzad.cpp:63 SPELL_DISPELL_SHACKLES ('not used, doing it manually'); no vmangos, mangos-classic or mangoszero source, DB script or spell list casts it"),
        new(28033, R.ScriptCarrier, "Aggro all in LOS: Sewage Slimes, Crypt Reavers, Ferra and Embalming Slimes cast it on themselves as a pull marker; its DUMMY effect has no handler in any core",
            "vmangos instance_naxxramas.cpp:214 casts it on every idle Sewage Slime ('aggro all in los'), creature_ai_scripts rows 1430803, 1597803 and 1602402 cast it; no core handles the effect"),
        new(28388, R.ScriptCarrier, "Random Aggro: the Naxxramas Risen Deathknight spell list casts it; its DUMMY effect has no handler in any core",
            "vmangos creature_spells row 161540 (Naxxramas - Risen Deathknight) lists it; no vmangos, mangos-classic or mangoszero source handles the effect"),
    ];
}
