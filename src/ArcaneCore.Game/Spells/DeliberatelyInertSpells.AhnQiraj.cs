using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>The deliberately inert spells of Ahn'Qiraj: content lanes add their entries here, never in the shared files.</summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhySwarmer = "nothing in vmangos, cmangos or mangoszero casts it; only azerothcore's 3.3.5 Ayamiss script runs the Hive'Zara swarmer loop (boss_ayamiss.cpp:46-51)";
    private const string CitSwarmer = "no vmangos, cmangos or mangoszero source or world DB script casts it; azerothcore boss_ayamiss.cpp:46-51 names it for a waypoint loop ArcaneCore's AyamissAI does not use (swarmers attack directly)";

    private static InertSpell[] BuildAhnQiraj() =>
    [
        new(16245, R.ScriptCarrier, "Freeze Anim: the C'Thun eye AI casts and removes it so the eye stops gazing around during Dark Glare; the DUMMY aura has no reader", "vmangos boss_cthun.cpp:52 SPELL_FREEZE_ANIMATION (cast :1266, removed :1217); no core handles the aura"),
        new(26235, R.Visual, "Cthun Vulnerable only adds the red tint; it makes nothing vulnerable (the carapace does)", "vmangos boss_cthun.cpp:82 comment, cast :1776, removed :1798; no core handles the aura"),
        new(25926, R.ScriptCarrier, "Viscidus Frost Weakness: the aura is only a marker, the frost-hit counting is done by the boss script", "vmangos UnitAuraProcHandler.cpp:699-701 has the proc case commented out ('handled on Viscidus script'); boss_viscidus.cpp:59"),
        new(25171, R.Visual, "Time Stop Visual DND: cast by Time Stop 25158's script and by Anachronos's script for the frozen look only", "vmangos silithus.cpp:1521 casts it for the visual; no core handles the aura"),
        new(25148, R.Visual, "Sleep Visual DND: the Darkshore NPC script casts and removes it for the sleeping look only", "vmangos darkshore.cpp:52 SPELL_SLEEP_VISUAL (cast :144, removed :149); no core handles the aura"),
        new(25711, R.NeverCast, WhySwarmer, CitSwarmer),
        new(25830, R.NeverCast, WhySwarmer, CitSwarmer),
        new(25833, R.NeverCast, WhySwarmer, CitSwarmer),
        new(25834, R.NeverCast, WhySwarmer, CitSwarmer),
        new(25835, R.NeverCast, WhySwarmer, CitSwarmer),
        new(25844, R.NeverCast, WhySwarmer, CitSwarmer),
    ];
}
