using R = ArcaneCore.Game.Spells.InertSpellReason;

namespace ArcaneCore.Game.Spells;

/// <summary>
/// The deliberately inert spells of world events: content lanes add their entries here, never in the shared files. Checked 2026-10-31 against the
/// vmangos and mangos-classic (cmangos) source trees, mangoszero and classic-db (ACID and Updates): every id below is either only cast or applied as a
/// marker or visual, or nothing in those trees handles or casts it. The spells of the lane that DO have a handler are scripts:
/// Lunar Teleport Caps 26406, 26408-26410, 26412 and 26414 (<c>LunarTeleportCapScript</c>) and Muglash's Brazier Trap 20863
/// (<c>MuglashBrazierTrapScript</c>).
/// </summary>
public static partial class DeliberatelyInertSpells
{
    private const string WhyWorldCarrier = "cast or applied by a creature AI or script for its visual or marker; the spell's own effect has no handler in any reference core";
    private const string WhyNoHandler = "no reference core handles or casts it, so its DUMMY effect has nothing to run";
    private const string CitNoHandler = "no case for the id in vmangos, mangos-classic or mangoszero source (whole-word search of src/, 2026-10-31)";

    private static InertSpell[] BuildWorldEvents() =>
    [
        new(26005, R.Marker, "Mistletoe marker aura: only cast by Mistletoe 26004, nothing reads it", "vmangos SpellEffects.cpp:4016-4023 casts 26005 on the target and nothing else references it"),
        new(24755, R.Marker, "Tricked or Treated marker aura: only cast by Trick or Treat 24751", "vmangos SpellEffects.cpp:3976-3988 casts 24755; its only reader is the cmangos ScriptDev2 innkeeper gossip (npcs_special.cpp:807), which is not a spell handler"),
        new(23770, R.Marker, "Sayge's Dark Fortune of *: the effect is skipped, only a common cooldown was ever imagined", "mangos-classic SpellEffects.cpp:1552-1554 and mangoszero SpellEffectDamageTeleport.cpp:407-409: just skip ('not exist, common cooldown can be implemented in scripts if need'); vmangos has no case"),
        new(23356, R.Marker, "Taming Lesson: only exempted from the target creature type mask, its DUMMY has no handler", "vmangos Spell.cpp:7595-7596 (mask ignored for 2641 and 23356); no SpellEffects case; ArcaneCore SpellSystem.TargetCreatureType.cs TamingLessonId"),
        new(26924, R.NeverCast, WhyNoHandler, "vmangos love_is_in_the_air.h:38 declares SPELL_VALENTINE 26924 with the comment Unknown; nothing casts or handles it"),
        new(26636, R.ScriptCarrier, "Elune's Candle firework: only cast by Elune's Candle 26374", "vmangos spell_item.cpp:553-574 (ElunesCandleScript) casts 26636; nothing handles it"),
        new(24999, R.ScriptCarrier, WhyWorldCarrier, "vmangos moonglade.cpp:268 AddAura(24999) on the summoned Malfurion"),
        new(17327, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:72 and desolace.cpp:309 (SPELL_BLUE_AURA, aura on the Magrami Spectre); mangos-classic spell_scripts.cpp:47; nothing handles the DUMMY"),
        new(31309, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:73 (SPELL_SPIRIT_PARTICLES_RED_BIG, Eastern Plaguelands tower visual); mangos-classic OutdoorPvPEP.h:61"),
        new(31951, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:75 (SPELL_SPIRIT_PARTICLES_RED_SUPER_BIG_DND, Eastern Plaguelands tower visual)"),
        new(31954, R.ScriptCarrier, WhyWorldCarrier, "vmangos OutdoorPvPEP.h:74 (SPELL_SPIRIT_PARTICLES_SUPER_BIG_DND, Eastern Plaguelands tower visual)"),
        new(20358, R.ScriptCarrier, WhyWorldCarrier, "vmangos quest_stormwind_rendezvous.cpp:595 (Windsor reads the tablets, cast at Onyxia)"),
        new(20465, R.ScriptCarrier, WhyWorldCarrier, "vmangos quest_stormwind_rendezvous.cpp:655 (Onyxia casts Windsor Death on Windsor)"),
        new(21896, R.ScriptCarrier, WhyWorldCarrier, "classic-db ACID/acid_classic.sql creature_ai_scripts row 1223801 (Zaetar's Spirit casts it on spawn); no core handles the spell"),
        new(27766, R.ScriptCarrier, WhyWorldCarrier, "vmangos spell_special.cpp:68 (Darkmoon Steam Tonk Cannon 24933 casts it on the target)"),
        new(27771, R.ScriptCarrier, WhyWorldCarrier, "vmangos spell_special.cpp:452 (SPELL_DAMAGED_TONK cast on the target of the Tonk script)"),
        new(7077, R.ScriptCarrier, WhyWorldCarrier, "vmangos dustwallow_marsh.cpp:47 and :153 (Tervosh teleport visual on his return)"),
        new(7141, R.ScriptCarrier, WhyWorldCarrier, "vmangos dustwallow_marsh.cpp:46 and :112 (Tervosh teleport visual on his arrival)"),
        new(20370, R.ScriptCarrier, WhyWorldCarrier, "vmangos PlayerBotMgr.cpp:1953-1966 (SPELL_RED_GLOW cast on a bot waypoint marker)"),
        new(7104, R.SpawnAura, WhySpawnAura, "classic-db Updates/4714_TDB-0542_quest_the_attack.sql creature_template_addon auras row for creature entry 1755; no core handles the spell"),
        new(6602, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(7143, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(7939, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(11013, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(21795, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(21830, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(21867, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(21914, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(21917, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(24746, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(24748, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(24782, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(11048, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(11067, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(22518, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(22581, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(22805, R.NeverCast, WhyNoHandler, CitNoHandler),
        new(24803, R.NeverCast, WhyNoHandler, CitNoHandler),
    ];
}
